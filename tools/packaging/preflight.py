#!/usr/bin/env python3
"""Validate owner-reviewed artifact receipts; never infer execution from symbols."""
import argparse
import hashlib
import json
import io
import re
from pathlib import Path, PurePosixPath
import struct
import sys
import xml.etree.ElementTree as ET
import zipfile

RIDS = {'linux-arm64', 'linux-x64', 'osx-arm64', 'osx-x64', 'win-arm64', 'win-x64'}
EXPORTS = {'Decoder_GetApiVersion', 'Decoder_SubmitPacket', 'Decoder_ReceivePcm', 'AacEncoder_GetTiming', 'Decoder_GetInputFormat'}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(data):
    return hashlib.sha256(data).hexdigest()


def safe_path(name):
    name = name.replace('\\', '/')
    path = PurePosixPath(name)
    require(name and not path.is_absolute() and '..' not in path.parts and
            not any(c in name for c in '*?$:;'), 'unsafe or wildcard path: ' + name)
    return str(path)


def read_artifact(root, record):
    path = root / safe_path(record['path'])
    require(path.resolve().is_relative_to(root.resolve()), 'artifact escapes staging root')
    data = path.read_bytes()
    require(digest(data) == record['sha256'], 'hash mismatch: ' + record['path'])
    return data


def architecture(data):
    # Reject fat Mach-O, 32-bit inputs and unknown executable formats.
    if data[:4] == b'\x7fELF' and data[4:6] == b'\x02\x01':
        cpu = struct.unpack_from('<H', data, 18)[0]
        return {62: 'linux-x64', 183: 'linux-arm64'}.get(cpu)
    if data[:4] == b'\xcf\xfa\xed\xfe':
        cpu = struct.unpack_from('<I', data, 4)[0]
        return {0x1000007: 'osx-x64', 0x100000c: 'osx-arm64'}.get(cpu)
    if data[:2] == b'MZ':
        offset = struct.unpack_from('<I', data, 60)[0]
        if data[offset:offset + 4] == b'PE\0\0':
            cpu = struct.unpack_from('<H', data, offset + 4)[0]
            return {0x8664: 'win-x64', 0xaa64: 'win-arm64'}.get(cpu)
    return None


def without_namespace(spec):
    for node in spec.iter():
        node.tag = node.tag.split('}')[-1]
    return spec


def parser_dependencies(spec, version):
    dependencies = spec.find('./metadata/dependencies')
    require(dependencies is not None, 'missing parser framework dependencies')
    require(not dependencies.findall('dependency'), 'parser dependencies require framework groups')
    groups = dependencies.findall('group')
    require(len(groups) == 2, 'expected exactly two parser framework groups')
    result = {}
    for group in groups:
        framework = group.attrib.get('targetFramework')
        require(framework in ('net8.0', 'net10.0') and framework not in result,
                'missing, duplicate or unsupported parser framework group')
        pins = [d.attrib.get('version') for d in group.findall('dependency')
                if d.attrib.get('id', '').lower() == 'aaxclean']
        require(pins == ['[' + version + ']'], 'nuspec parser dependency must be exact for both TFMs')
        result[framework] = pins[0]
    return result


def validate(root, manifest, nuspec, version, archive=None):
    require(manifest['schema'] == 1, 'unsupported manifest schema')
    require(re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?', version), 'invalid package version')
    require(manifest['version'] == version and version != '3.1.0', 'version mismatch or immutable 3.1.0')
    lane = manifest['lane']
    require(lane in ('local', 'shipping'), 'unknown lane')
    if lane == 'local':
        require('-local.' in version, 'local lane requires unique -local. version')
    else:
        require('-local.' not in version, 'local version cannot ship')
    files = manifest['files']
    expected = {}
    natives = []
    managed = {}
    for record in files:
        target = safe_path(record['target'])
        require(target not in expected, 'duplicate package target: ' + target)
        data = read_artifact(root, record)
        expected[target] = record['sha256']
        if target.startswith('runtimes/') and '/native/' in target:
            natives.append((record, data))
        elif target.endswith('.dll'):
            managed[target] = record['sha256']
    required_managed = {f'lib/{tfm}/{dll}.dll' for tfm in ('net8.0', 'net10.0')
                        for dll in ('AAXClean.Codecs', 'NAudio.Lame')}
    require(required_managed <= managed.keys(), 'missing managed target assemblies')
    parser = manifest['parser']
    parser_bytes = read_artifact(root, parser)
    with zipfile.ZipFile(io.BytesIO(parser_bytes)) as package:
        specs = [n for n in package.namelist() if n.endswith('.nuspec')]
        require(len(specs) == 1, 'parser package must have one nuspec')
        metadata = ET.fromstring(package.read(specs[0]))
        values = {e.tag.split('}')[-1]: e.text for e in metadata.iter()}
        require(values.get('id') == 'AAXClean' and values.get('version') == parser['version'], 'parser identity mismatch')
    require(parser['version'] != '3.1.0', 'accepted parser package must be explicitly pinned')
    rids = []
    for record, data in natives:
        rid = record['target'].split('/')[1]
        require(rid in RIDS and architecture(data) == rid, 'native architecture mismatch')
        filename = 'aaxcleannative.dll' if rid.startswith('win') else 'libaaxcleannative.' + ('dylib' if rid.startswith('osx') else 'so')
        require(record['target'] == f'runtimes/{rid}/native/{filename}', 'unexpected native filename')
        rids.append(rid)
        receipt = json.loads(read_artifact(root, record['receipt']))
        require(receipt['rid'] == rid and receipt['native_sha256'] == record['sha256'] and
                receipt['managed'] == managed and receipt['parser_sha256'] == parser['sha256'],
                'stale managed/native/parser pairing')
        require(receipt['api_version'] == 2 and EXPORTS <= set(receipt['exports']), 'missing v2 exports/version')
        require(receipt['execution_host_rid'] == rid and receipt['drain_pass'] is True and
                receipt['error_pass'] is True, 'missing matching RID drain/error execution')
        require(receipt.get('encoder_timing_pass') is True, 'missing matching RID encoder timing execution')
        require(receipt.get('input_format_pass') is True, 'missing matching RID decoded input-format execution')
        for field in ('source', 'dependencies', 'configuration', 'toolchain'):
            require(isinstance(receipt[field], str) and receipt[field].strip(), 'missing provenance: ' + field)
        read_artifact(root, receipt['execution_log'])
        disposition = receipt['distribution']
        require(disposition in ('approved', 'NONFREE/UNREDISTRIBUTABLE'), 'unknown distribution disposition')
        if lane == 'shipping':
            require(disposition == 'approved', 'distribution not approved')
            read_artifact(root, receipt['distribution_record'])
    require(len(rids) == len(set(rids)), 'duplicate RID')
    require(set(rids) == RIDS if lane == 'shipping' else len(rids) == 1, 'missing or unexpected RID set')
    spec = without_namespace(ET.parse(nuspec).getroot())
    actual = {}
    for entry in spec.findall('./files/file'):
        target = safe_path(entry.attrib['target'])
        source = safe_path(entry.attrib['src'])
        require(target not in actual, 'duplicate nuspec target')
        actual[target] = digest((root / source).read_bytes())
    require(actual == expected, 'nuspec inputs differ from attested manifest')
    versions = parser_dependencies(spec, parser['version'])
    require(spec.findtext('./metadata/id') == 'AAXClean.Codecs', 'wrong package id')
    require(spec.findtext('./metadata/version') in (version, '$version$'), 'nuspec version mismatch')
    if archive:
        with zipfile.ZipFile(archive) as package:
            names = package.namelist()
            require(len(names) == len(set(names)), 'duplicate archive entry')
            for name, sha in expected.items():
                require(digest(package.read(name)) == sha, 'archive hash mismatch: ' + name)
            payload = {n for n in names if n.startswith(('lib/', 'runtimes/'))}
            require(payload == {n for n in expected if n.startswith(('lib/', 'runtimes/'))}, 'unexpected archive payload')
            specs = [n for n in names if n.endswith('.nuspec')]
            require(len(specs) == 1, 'archive must have one nuspec')
            packed = without_namespace(ET.fromstring(package.read(specs[0])))
            tags = lambda name: [e for e in packed.iter() if e.tag.split('}')[-1] == name]
            require([e.text for e in tags('id')] == ['AAXClean.Codecs'] and
                    [e.text for e in tags('version')] == [version], 'archive package identity mismatch')
            deps = parser_dependencies(packed, parser['version'])
            require(deps == versions, 'archive parser dependency mismatch')


def main():
    cli = argparse.ArgumentParser(description=__doc__)
    cli.add_argument('--root', type=Path, required=True)
    cli.add_argument('--manifest', type=Path, required=True)
    cli.add_argument('--nuspec', type=Path, required=True)
    cli.add_argument('--version', required=True)
    cli.add_argument('--archive', type=Path)
    args = cli.parse_args()
    try:
        validate(args.root, json.loads(args.manifest.read_text()), args.nuspec, args.version, args.archive)
    except (ValueError, KeyError, OSError, TypeError, struct.error, ET.ParseError, zipfile.BadZipFile) as error:
        print('Package admission rejected: ' + str(error), file=sys.stderr)
        return 1
    print('Package admission passed (artifact receipts; no new RID execution).')
    return 0


if __name__ == '__main__':
    sys.exit(main())
