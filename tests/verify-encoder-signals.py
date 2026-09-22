#!/usr/bin/env python3
"""Independently decode EncoderSignalTests artifacts with FFmpeg and NumPy.
Usage: python3 tests/verify-encoder-signals.py <encoder-signals-directory> <receipt.json>
No source/provider fixtures or network access are used. A fresh success receipt is
written only after all 12 synthetic outputs satisfy duration and signal checks.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import numpy as np


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def cosine(a, b):
    denominator = np.linalg.norm(a) * np.linalg.norm(b)
    require(denominator > 0, 'silent comparison cannot establish alignment')
    return float(a @ b / denominator)


def verify(root, output, ffmpeg):
    output.unlink(missing_ok=True)
    expected = {f'single-{n}-16000-1' for n in (1, 128, 1024, 1501, 16001)}
    expected |= {f'single-{n}-44100-2' for n in (128, 1024, 1501, 16001)}
    expected |= {f'chapter-{i}' for i in range(3)}
    manifests = {p.stem: p for p in root.glob('*.json')}
    require(set(manifests) == expected, 'missing, stale or unexpected signal fixtures')
    results = []
    for name, path in sorted(manifests.items()):
        meta = json.loads(path.read_text())
        mp4, pcm = path.with_suffix('.m4a'), path.with_suffix('.s16')
        require(digest(mp4) == meta['mp4_sha256'] and digest(pcm) == meta['pcm_sha256'], 'changed signal fixture')
        samples, channels = meta['samples'], meta['channels']
        reference = np.frombuffer(pcm.read_bytes(), dtype='<i2').astype(np.float64).reshape((-1, channels)) / 32768
        require(len(reference) == samples, 'source PCM count mismatch')
        decoded = {}
        for mode in ('presented', 'media'):
            options = ['-ignore_editlist', '1'] if mode == 'media' else []
            command = [ffmpeg, '-v', 'error', '-xerror', *options, '-i', str(mp4), '-map', '0:a:0', '-f', 'f32le', '-']
            run = subprocess.run(command, capture_output=True, timeout=30)
            require(run.returncode == 0 and not run.stderr, f'{name}: independent decoder failure: {run.stderr.decode(errors="replace")}')
            decoded[mode] = np.frombuffer(run.stdout, dtype='<f4').astype(np.float64).reshape((-1, channels))
            require(np.isfinite(decoded[mode]).all(), 'nonfinite decoded signal')
        presented, media = decoded['presented'], decoded['media']
        require(len(presented) == samples, f'{name}: decoder presents {len(presented)} samples, expected {samples}')
        require(len(media) == meta['media_samples'], f'{name}: media sample count mismatch')
        measurements = []
        for channel in range(channels):
            source, actual = reference[:, channel], presented[:, channel]
            if samples == 1:
                require(abs(actual[0] - source[0]) < .04, 'single-sample amplitude does not survive presentation')
                measurements.append({'channel': channel, 'single_sample_absolute_error': float(abs(actual[0] - source[0]))})
                continue
            # Search the entire raw decoded timeline; no encoder delay is supplied
            # to the correlation. A wrong reported delay therefore fails this test.
            lag = int(np.correlate(media[:, channel], source, mode='valid').argmax())
            require(lag == meta['delay'], f'{name}: independently measured delay {lag} differs from {meta["delay"]}')
            width = min(256, samples)
            scores = {'whole': cosine(actual, source), 'head': cosine(actual[:width], source[:width]),
                      'tail': cosine(actual[-width:], source[-width:])}
            require(min(scores.values()) > .98, f'{name}: signal does not align: {scores}')
            measurements.append({'channel': channel, 'measured_delay': lag, 'cosine': scores})
        results.append({**meta, 'manifest_sha256': digest(path), 'measurements': measurements,
                        'decoded_presented_samples': len(presented), 'decoded_media_samples': len(media)})
    receipt = {'schema': 1, 'independent_decoder': subprocess.check_output([ffmpeg, '-version'], text=True).splitlines()[0],
               'verifier_sha256': digest(Path(__file__)), 'numpy': np.__version__, 'results': results,
               'limits': '12 synthetic AAC-LC outputs on the observed host; no other profiles, providers, platforms or listening acceptance'}
    output.write_text(json.dumps(receipt, indent=2) + '\n')
    print(f'PASS: {len(results)} independently decoded files; exact presentation counts and head/tail/chapter alignment')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('fixtures', type=Path)
    parser.add_argument('receipt', type=Path)
    parser.add_argument('--ffmpeg', default=shutil.which('ffmpeg'))
    args = parser.parse_args()
    require(args.ffmpeg, 'an independent FFmpeg executable is required')
    verify(args.fixtures.resolve(), args.receipt.resolve(), args.ffmpeg)
