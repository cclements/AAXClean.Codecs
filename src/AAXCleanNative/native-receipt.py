#!/usr/bin/env python3
"""Record a pinned build and actual matching-host native checks; never admit shipping."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import subprocess
import sys

RIDS = {'osx-arm64', 'osx-x64', 'linux-arm64', 'linux-x64', 'win-arm64', 'win-x64'}
EXPORTS = ('Decoder_GetApiVersion', 'Decoder_SubmitPacket', 'Decoder_ReceivePcm')


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def host_rid():
    system = platform.system()
    prefix = 'win' if system.startswith(('Windows', 'MINGW', 'MSYS')) else {'Darwin': 'osx', 'Linux': 'linux'}.get(system)
    cpu = {'arm64': 'arm64', 'aarch64': 'arm64', 'x86_64': 'x64', 'amd64': 'x64'}.get(platform.machine().lower())
    require(prefix and cpu, 'unsupported execution host')
    return f'{prefix}-{cpu}'


def native_name(rid):
    return 'aaxcleannative.dll' if rid.startswith('win-') else 'libaaxcleannative.' + ('dylib' if rid.startswith('osx-') else 'so')


def checked_hashes(root, records):
    for name, sha in records.items():
        path = root / name
        require(path.resolve().is_relative_to(root.resolve()), 'receipt path escapes root')
        require(digest(path) == sha, f'changed receipt input: {name}')


def write_json(path, value):
    temporary = path.with_suffix('.tmp')
    temporary.write_text(json.dumps(value, indent=2) + '\n')
    temporary.replace(path)


def record_build(source, build, rid, mode):
    require(rid in RIDS and host_rid() == rid, 'build RID does not match execution host')
    spec = importlib.util.spec_from_file_location('package_preflight', source.parents[1] / 'tools/packaging/preflight.py')
    gate = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(gate)
    native = build / native_name(rid)
    require(gate.architecture(native.read_bytes()) == rid, 'built architecture mismatch')
    exports = (build / 'exports.txt').read_text()
    require(all(name in exports for name in EXPORTS), 'missing native v2 export inspection')
    manifest = json.loads((source / 'native-sources.json').read_text())
    source_paths = ['AAXCleanNative.h', 'AacDecoder.c', 'AacEncoder.c', 'build-native.sh',
                    'prepare-native-sources.py', 'native-sources.json', 'native-receipt.py',
                    'tests/drain-tests.c', 'tests/drain-format-tests.c']
    paths = [native, build / 'toolchain.txt', build / 'exports.txt', build / 'linked-libraries.txt',
             build / 'librempeg/config.h', build / 'librempeg/ffbuild/config.mak',
             build / 'drain-tests', build / 'drain-format-tests']
    if rid.startswith('win-'):
        paths[-2:] = [build / 'drain-tests.exe', build / 'drain-format-tests.exe']
    libraries = sorted((build / 'prefix/lib').glob('*.a'))
    require(len(libraries) >= 6, 'missing pinned static libraries')
    paths += libraries
    # Record the actual downloaded archives as well as their manifest pins.
    for item in manifest['sources']:
        require(digest(build.parent / 'downloads' / item['archive']) == item['sha256'], 'changed dependency archive')
    value = {'schema': 1, 'rid': rid, 'mode': mode, 'distribution': 'NONFREE/UNREDISTRIBUTABLE',
             'source_commit': subprocess.check_output(['git', '-C', str(source), 'rev-parse', 'HEAD'], text=True).strip(),
             'source_files': {name: digest(source / name) for name in source_paths},
             'dependencies': manifest['sources'],
             'files': {p.relative_to(build).as_posix(): digest(p) for p in paths},
             'package_admission': 'NOT ADMITTED: managed/parser pairing and distribution review are separate'}
    # Preserve the immutable dependency list consumed by the original safety and
    # decoder regression runners. It also binds the exact vendor headers.
    vendor = build.parent / 'sources' / next(s['directory'] for s in manifest['sources'] if s['name'] == 'librempeg')
    legacy_paths = paths + [source / name for name in source_paths]
    legacy_paths += [vendor / name for name in ['libavcodec/avcodec.h', 'libavcodec/packet.h', 'libavcodec/packet.c']]
    (build / 'sha256.txt').write_text(''.join(f'{digest(path)}  {path}\n' for path in legacy_paths))
    write_json(build / 'build-receipt.json', value)


def verify(source, build, rid, fixtures):
    output = build / 'native-verification.json'
    output.unlink(missing_ok=True)
    require(host_rid() == rid, 'cannot execute or attest a foreign RID')
    receipt = json.loads((build / 'build-receipt.json').read_text())
    require(receipt['rid'] == rid, 'stale build RID')
    checked_hashes(source, receipt['source_files'])
    checked_hashes(build, receipt['files'])
    fixture = json.loads((fixtures / 'manifest.json').read_text())
    checked_hashes(fixtures, fixture['files'])
    require(digest((fixtures / fixture['generator']).resolve()) == fixture['generator_sha256'], 'changed fixture generator')
    suffix = '.exe' if rid.startswith('win-') else ''
    runner = build / ('drain-tests' + suffix)
    native = build / native_name(rid)
    results = {}
    logs = {}
    environment = os.environ.copy()
    if receipt['mode'] == 'asan' and rid.startswith('osx-'):
        environment['ASAN_OPTIONS'] = 'detect_leaks=0:abort_on_error=1'

    def run(name, arguments):
        result = subprocess.run(list(map(str, arguments)), stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                text=True, timeout=90, env=environment)
        log = build / f'verify-{name}.log'
        log.write_text(result.stdout)
        require(result.returncode == 0, f'{name} execution failed: {result.returncode}; see {log}')
        logs[log.name] = digest(log)
        return result.stdout

    for codec, count in fixture['samples_per_channel'].items():
        text = run(codec, [runner, native, fixtures, codec, build / f'verify-{codec}.s16', 'v2'])
        result = json.loads(text)
        require(result['codec'] == codec and result['legacy'] is False and result['finalState'] == 3,
                f'{codec} did not reach v2 EOF')
        require(result['samplesPerChannel'] == count and result['receiveFirst'] > 0,
                f'{codec} incorrect drain/EAGAIN result')
        require((build / f'verify-{codec}.s16').stat().st_size == count * 4, 'PCM byte count mismatch')
        results[codec] = result
    text = run('rate-change', [runner, native, fixtures, 'eac3-rate-change', build / 'verify-rejected.s16', 'reject-change'])
    require('PASS: real coded format change rejected terminally' in text, 'missing coded format-change witness')
    text = run('format-changes', [build / ('drain-format-tests' + suffix)])
    require('PASS: rate, sample format and layout changes fail terminally before conversion' in text,
            'missing rate/format/layout witness')
    # Ensure a test process did not replace the candidate or its configuration.
    checked_hashes(source, receipt['source_files'])
    checked_hashes(build, receipt['files'])
    value = {'schema': 1, 'rid': rid, 'execution_host_rid': host_rid(), 'api_version': 2,
             'exports': list(EXPORTS), 'native_sha256': digest(native),
             'build_receipt_sha256': digest(build / 'build-receipt.json'),
             'fixture_manifest_sha256': digest(fixtures / 'manifest.json'),
             'drain_pass': True, 'error_pass': True, 'results': results, 'execution_logs': logs,
             'distribution': 'NONFREE/UNREDISTRIBUTABLE',
             'package_admission': 'NOT ADMITTED: no managed/parser package-pair execution receipt',
             'limits': 'AAC-LC and E-AC-3 synthetic counts; no real AC-4/HE-AAC/xHE-AAC, alignment, managed runtime or distribution proof'}
    write_json(output, value)
    print(f'{rid}: three native drain cases and two terminal-error suites passed')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['build', 'verify'])
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--build', type=Path, required=True)
    parser.add_argument('--rid', choices=sorted(RIDS), required=True)
    parser.add_argument('--mode', choices=['release', 'asan'], default='release')
    parser.add_argument('--fixtures', type=Path)
    args = parser.parse_args()
    source, build = args.source.resolve(), args.build.resolve()
    try:
        if args.action == 'build':
            record_build(source, build, args.rid, args.mode)
        else:
            verify(source, build, args.rid, (args.fixtures or source / 'tests/fixtures').resolve())
    except (ValueError, KeyError, OSError, subprocess.SubprocessError) as error:
        print(f'Native receipt rejected: {error}', file=sys.stderr)
        return 1
    return 0


if __name__ == '__main__':
    sys.exit(main())
