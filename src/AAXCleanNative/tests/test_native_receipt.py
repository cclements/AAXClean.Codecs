"""Fail-closed receipt guards; synthetic gate tests are not native runtime proof."""
import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import subprocess
import unittest
from unittest.mock import patch

SOURCE = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('native_receipt', SOURCE / 'native-receipt.py')
receipt = importlib.util.module_from_spec(spec)
spec.loader.exec_module(receipt)


class ReceiptTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def test_changed_file_rejected(self):
        path = self.root / 'payload'
        path.write_bytes(b'old')
        hashes = {'payload': receipt.digest(path)}
        path.write_bytes(b'new')
        with self.assertRaisesRegex(ValueError, 'changed receipt input'):
            receipt.checked_hashes(self.root, hashes)

    def test_escape_rejected(self):
        with self.assertRaisesRegex(ValueError, 'escapes root'):
            receipt.checked_hashes(self.root, {'../payload': '0' * 64})

    def test_symlink_escape_rejected(self):
        (self.root / 'outside').symlink_to(self.root.parent)
        with self.assertRaisesRegex(ValueError, 'escapes root'):
            receipt.checked_hashes(self.root, {'outside/payload': '0' * 64})

    def test_missing_file_rejected(self):
        with self.assertRaises(FileNotFoundError):
            receipt.checked_hashes(self.root, {'missing': '0' * 64})

    def test_unchanged_file(self):
        (self.root / 'input').write_bytes(b'verified')
        receipt.checked_hashes(self.root, {'input': hashlib.sha256(b'verified').hexdigest()})

    def test_foreign_host_invalidates_previous_success(self):
        (self.root / 'native-verification.json').write_text('{"drain_pass": true}')
        with patch.object(receipt, 'host_rid', return_value='linux-x64'):
            with self.assertRaisesRegex(ValueError, 'foreign RID'):
                receipt.verify(SOURCE, self.root, 'osx-arm64', SOURCE / 'tests/fixtures')
        self.assertFalse((self.root / 'native-verification.json').exists())

    def test_stale_build_rid_rejected(self):
        (self.root / 'build-receipt.json').write_text(json.dumps({'rid': 'linux-arm64'}))
        with patch.object(receipt, 'host_rid', return_value='osx-arm64'):
            with self.assertRaisesRegex(ValueError, 'stale build RID'):
                receipt.verify(SOURCE, self.root, 'osx-arm64', SOURCE / 'tests/fixtures')

    def test_failed_native_process_cannot_issue_success(self):
        (self.root / 'build-receipt.json').write_text(json.dumps({'rid': 'osx-arm64', 'source_files': {}, 'files': {}, 'mode': 'release'}))
        (self.root / 'native-verification.json').write_text('{"drain_pass": true}')
        with patch.object(receipt, 'host_rid', return_value='osx-arm64'), patch.object(receipt.subprocess, 'run', return_value=subprocess.CompletedProcess([], 1, 'native failure')):
            with self.assertRaisesRegex(ValueError, 'execution failed'):
                receipt.verify(SOURCE, self.root, 'osx-arm64', SOURCE / 'tests/fixtures')
        self.assertFalse((self.root / 'native-verification.json').exists())
        self.assertEqual((self.root / 'verify-aac.log').read_text(), 'native failure')

    def test_successful_process_with_wrong_sample_count_is_rejected(self):
        (self.root / 'build-receipt.json').write_text(json.dumps({'rid': 'osx-arm64', 'source_files': {}, 'files': {}, 'mode': 'release'}))
        wrong = json.dumps({'codec': 'aac', 'legacy': False, 'finalState': 3, 'samplesPerChannel': 1024, 'receiveFirst': 1})
        with patch.object(receipt, 'host_rid', return_value='osx-arm64'), patch.object(receipt.subprocess, 'run', return_value=subprocess.CompletedProcess([], 0, wrong)):
            with self.assertRaisesRegex(ValueError, 'incorrect drain'):
                receipt.verify(SOURCE, self.root, 'osx-arm64', SOURCE / 'tests/fixtures')
        self.assertFalse((self.root / 'native-verification.json').exists())

    def test_fixture_manifest_matches_committed_inputs(self):
        fixtures = SOURCE / 'tests/fixtures'
        manifest = json.loads((fixtures / 'manifest.json').read_text())
        receipt.checked_hashes(fixtures, manifest['files'])
        self.assertEqual(receipt.digest((fixtures / manifest['generator']).resolve()), manifest['generator_sha256'])

    def test_platform_architecture_mapping(self):
        for system, machine, expected in [('Windows', 'ARM64', 'win-arm64'), ('Linux', 'x86_64', 'linux-x64'),
                                         ('Darwin', 'arm64', 'osx-arm64')]:
            with self.subTest(expected=expected), patch.object(receipt.platform, 'system', return_value=system), patch.object(receipt.platform, 'machine', return_value=machine):
                self.assertEqual(receipt.host_rid(), expected)

    def test_unknown_host_rejected(self):
        with patch.object(receipt.platform, 'system', return_value='unknown'):
            with self.assertRaisesRegex(ValueError, 'unsupported execution host'):
                receipt.host_rid()


if __name__ == '__main__':
    unittest.main()
