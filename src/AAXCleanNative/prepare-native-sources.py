#!/usr/bin/env python3
"""Fetch exact native inputs with verified TLS and SHA-256; publish extracted trees atomically."""
import hashlib
import json
from pathlib import Path
import shutil
import sys
import tarfile
import tempfile
import urllib.request


def verify_tree(archive, destination, top):
    """Refuse modified extracted inputs, even when the cached archive is intact."""
    expected = set()
    with tarfile.open(archive) as package:
        for member in package.getmembers():
            relative = Path(member.name).relative_to(top)
            if not relative.parts:
                continue
            expected.add(relative)
            target = destination / relative
            if member.isfile():
                if target.is_symlink() or not target.is_file():
                    raise RuntimeError(f'missing/changed source file: {target}')
                with package.extractfile(member) as original:
                    if hashlib.sha256(original.read()).digest() != hashlib.sha256(target.read_bytes()).digest():
                        raise RuntimeError(f'modified source file: {target}')
            elif member.isdir():
                if target.is_symlink() or not target.is_dir():
                    raise RuntimeError(f'missing/changed source directory: {target}')
            elif member.issym():
                if not target.is_symlink() or str(target.readlink()) != member.linkname:
                    raise RuntimeError(f'modified source link: {target}')
            else:
                raise RuntimeError(f'unsupported archive member: {member.name}')
    actual = {path.relative_to(destination) for path in destination.rglob('*')}
    if actual != expected:
        raise RuntimeError(f'unexpected extracted source contents: {destination}: {actual - expected}')


def prepare(root):
    manifest = json.loads(Path(__file__).with_name('native-sources.json').read_text())
    downloads, sources = root / 'downloads', root / 'sources'
    downloads.mkdir(parents=True, exist_ok=True)
    sources.mkdir(parents=True, exist_ok=True)
    for entry in manifest['sources']:
        archive = downloads / entry['archive']
        if not archive.exists():
            with urllib.request.urlopen(entry['url'], timeout=90) as response:
                data = response.read()
            if hashlib.sha256(data).hexdigest() != entry['sha256']:
                raise RuntimeError(f"download hash mismatch: {entry['name']}")
            temporary = archive.with_suffix(archive.suffix + '.tmp')
            temporary.write_bytes(data)
            temporary.replace(archive)
        if hashlib.sha256(archive.read_bytes()).hexdigest() != entry['sha256']:
            raise RuntimeError(f"cached archive hash mismatch: {entry['name']}")
        destination = sources / entry['directory']
        if not destination.exists():
            with tempfile.TemporaryDirectory(dir=sources) as temporary:
                with tarfile.open(archive) as package:
                    package.extractall(temporary, filter='data')
                extracted = Path(temporary) / entry['directory']
                extracted.rename(destination)
        verify_tree(archive, destination, entry['directory'])
        print(f"{entry['name']}: {entry['revision']} ({entry['sha256']})")
    shutil.copyfile(Path(__file__).with_name('native-sources.json'), root / 'native-sources.json')


if __name__ == '__main__':
    if len(sys.argv) != 2:
        raise SystemExit('usage: prepare-native-sources.py <artifact-root>')
    prepare(Path(sys.argv[1]).resolve())
