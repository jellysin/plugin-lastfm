"""The exact-commit rebuilds must prove the bytes selected for publication."""

import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

from tools import reproduce


class ReproduceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.dist = self.root / "dist"
        self.dist.mkdir()
        for name in ("plugin-1.0.0.zip", "release.json", "sbom.spdx.json", "checksums.txt"):
            (self.dist / name).write_bytes(b"original-" + name.encode())

    def test_identical_rebuilds_do_not_certify_different_upload_bytes(self):
        expected = reproduce.artifacts(self.dist)
        changed = dict(expected, **{"plugin-1.0.0.zip": "different"})
        with self.assertRaisesRegex(RuntimeError, "selected for upload differ"):
            reproduce.verify_results(expected, expected, changed)
        with self.assertRaisesRegex(RuntimeError, "different release bytes across"):
            reproduce.verify_results(expected, changed, expected)
        reproduce.verify_results(expected, expected, expected)

    def test_unexpected_or_empty_artifacts_are_rejected(self):
        extra = self.dist / "unreviewed.dll"
        extra.write_bytes(b"not an approved artifact")
        with self.assertRaisesRegex(RuntimeError, "unexpected artifact set"):
            reproduce.artifacts(self.dist)
        extra.unlink()
        (self.dist / "release.json").write_bytes(b"")
        with self.assertRaisesRegex(RuntimeError, "unexpected artifact set"):
            reproduce.artifacts(self.dist)

    def git(self, _directory, *arguments):
        if arguments == ("rev-parse", "--show-toplevel"):
            return str(self.root)
        if arguments[0] == "status":
            return ""
        return "a" * 40

    def arguments(self):
        return ["reproduce.py", "--tooling-directory", str(self.root), "--tag", "v1.0.0",
                "--reference-directory", str(self.dist), "--report", str(self.root / "report.json")]

    def test_tag_commit_mismatch_stops_before_any_rebuild(self):
        def git(directory, *arguments):
            if "--verify" in arguments:
                return "b" * 40
            return self.git(directory, *arguments)

        with patch("sys.argv", self.arguments()), patch.object(reproduce, "git", side_effect=git), \
                patch.object(reproduce, "build") as build:
            with self.assertRaisesRegex(RuntimeError, "does not match the exact release tag"):
                reproduce.main()
            build.assert_not_called()

    def test_selected_upload_changes_during_rebuild_are_rejected(self):
        expected = reproduce.artifacts(self.dist)

        def build(*_arguments):
            (self.dist / "plugin-1.0.0.zip").write_bytes(b"changed during rebuild")
            return expected

        with patch("sys.argv", self.arguments()), patch.object(reproduce, "git", side_effect=self.git), \
                patch.object(reproduce, "build", side_effect=build):
            with self.assertRaisesRegex(RuntimeError, "changed during the reproducibility check"):
                reproduce.main()
        self.assertFalse((self.root / "report.json").exists())

    def test_success_report_identifies_the_exact_tag_and_verified_upload_set(self):
        expected = reproduce.artifacts(self.dist)
        with patch("sys.argv", self.arguments()), patch.object(reproduce, "git", side_effect=self.git), \
                patch.object(reproduce, "build", return_value=expected), patch("builtins.print"):
            self.assertEqual(0, reproduce.main())
        report = json.loads((self.root / "report.json").read_text(encoding="utf-8"))
        self.assertTrue(report["matchesSelectedArtifacts"])
        self.assertEqual("v1.0.0", report["tag"])
        self.assertEqual(expected, report["matchingArtifactSha256"])


if __name__ == "__main__":
    unittest.main()
