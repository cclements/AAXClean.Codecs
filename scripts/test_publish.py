"""Hermetic process-level publication checks; no network or real credential."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]

class PublishContractTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.package = self.root / "local candidate.nupkg"
        self.package.write_bytes(b"synthetic package")
        self.args = self.root / "arguments.json"
        cli = self.root / "dotnet"
        cli.write_text("#!/usr/bin/env python3\nimport json, os, sys\nfrom pathlib import Path\nPath(os.environ['CAPTURE_ARGS']).write_text(json.dumps(sys.argv[1:]))\nraise SystemExit(int(os.environ.get('PUSH_EXIT', '0')))\n")
        cli.chmod(0o755)
        self.env = {**os.environ, "PATH": str(self.root) + os.pathsep + os.environ["PATH"],
                    "CAPTURE_ARGS": str(self.args), "NUGET_API_KEY": "synthetic-only-fixture"}

    def run_publish(self):
        return subprocess.run(["bash", str(ROOT / "scripts/publish-package.sh"),
                               str(self.package), "https://package-fixture.invalid/v3/index.json"],
                              env=self.env, text=True, capture_output=True)

    def test_missing_credential_never_invokes_client(self):
        self.env.pop("NUGET_API_KEY")
        result = self.run_publish()
        self.assertEqual(64, result.returncode)
        self.assertFalse(self.args.exists())

    def test_missing_archive_never_invokes_client(self):
        self.package.unlink()
        result = self.run_publish()
        self.assertEqual(66, result.returncode)
        self.assertFalse(self.args.exists())

    def test_success_preserves_literal_arguments_and_does_not_skip_duplicates(self):
        result = self.run_publish()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["nuget", "push", str(self.package), "--source",
                          "https://package-fixture.invalid/v3/index.json", "--api-key",
                          "synthetic-only-fixture"], json.loads(self.args.read_text()))
        self.assertNotIn("synthetic-only-fixture", result.stdout + result.stderr)

    def test_rejected_push_cannot_become_success(self):
        for exit_code in (1, 17):
            with self.subTest(exit_code=exit_code):
                self.env["PUSH_EXIT"] = str(exit_code)
                self.assertEqual(exit_code, self.run_publish().returncode)

if __name__ == "__main__":
    unittest.main()
