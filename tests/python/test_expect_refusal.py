"""Refusal expectation runner verifies that planted defects exit 1 with expected report failure."""

from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))
sys.path.insert(0, str(ROOT / "tools"))
import expect_refusal
from console import run


def child_command(report_path: Path, failure_text: str | None, passed: bool | None, exit_code: int) -> list[str]:
    """A child process that writes the given report fields, if any, and exits with exit_code."""
    data: dict[str, object] = {}
    if failure_text is not None:
        data["failure"] = failure_text
    if passed is not None:
        data["passed"] = passed
    raw_json = json.dumps(data)
    code = (
        "import json, sys, pathlib; "
        f"path = pathlib.Path({str(report_path)!r}); "
        f"data = json.loads({raw_json!r}); "
        "path.write_text(json.dumps(data), encoding='utf-8') if data else None; "
        f"sys.exit({exit_code})"
    )
    return [sys.executable, "-c", code]


def no_report_command(exit_code: int) -> list[str]:
    """A child process that writes no report and exits with exit_code."""
    return [sys.executable, "-c", f"import sys; sys.exit({exit_code})"]


class ExpectRefusalTests(unittest.TestCase):
    def test_positive_rc1_with_expected_failure_passes(self):
        """Exit status 1 with the expected failure text counts as a refusal."""
        with tempfile.TemporaryDirectory() as temp_dir:
            report_file = Path(temp_dir) / "report.json"
            cmd = child_command(report_file, "After the restore the account reads error", False, 1)
            rc, output, _errors = run(expect_refusal.main, [
                "--report", str(report_file),
                "--expect", "After the restore the account reads",
                "--", *cmd,
            ])
            self.assertEqual(rc, 0)
            self.assertIn("PASS:", output)

    def test_negative_rc0_fails(self):
        """A command that passes is no refusal."""
        with tempfile.TemporaryDirectory() as temp_dir:
            report_file = Path(temp_dir) / "report.json"
            cmd = child_command(report_file, "Some failure text", False, 0)
            rc, _output, errors = run(expect_refusal.main, [
                "--report", str(report_file),
                "--expect", "Some failure",
                "--", *cmd,
            ])
            self.assertEqual(rc, 1)
            self.assertIn("FAIL:", errors)

    def test_negative_rc1_with_other_failure_text_fails(self):
        """A failure for another reason than the expected one is no refusal."""
        with tempfile.TemporaryDirectory() as temp_dir:
            report_file = Path(temp_dir) / "report.json"
            cmd = child_command(report_file, "Completely different failure", False, 1)
            rc, _output, errors = run(expect_refusal.main, [
                "--report", str(report_file),
                "--expect", "After the restore the account reads",
                "--", *cmd,
            ])
            self.assertEqual(rc, 1)
            self.assertIn("FAIL:", errors)

    def test_negative_rc2_fails(self):
        """An exit status other than 1, such as a usage error, is no refusal."""
        with tempfile.TemporaryDirectory() as temp_dir:
            report_file = Path(temp_dir) / "report.json"
            cmd = child_command(report_file, "Failure message", False, 2)
            rc, _output, errors = run(expect_refusal.main, [
                "--report", str(report_file),
                "--expect", "Failure message",
                "--", *cmd,
            ])
            self.assertEqual(rc, 1)
            self.assertIn("FAIL:", errors)

    def test_negative_missing_report_fails(self):
        """A command that writes no report is no refusal."""
        with tempfile.TemporaryDirectory() as temp_dir:
            report_file = Path(temp_dir) / "report.json"
            cmd = no_report_command(1)
            rc, _output, errors = run(expect_refusal.main, [
                "--report", str(report_file),
                "--expect", "Failure message",
                "--", *cmd,
            ])
            self.assertEqual(rc, 1)
            self.assertIn("FAIL:", errors)

    def test_negative_passed_true_fails(self):
        """A report that records a pass is no refusal, whatever the exit status."""
        with tempfile.TemporaryDirectory() as temp_dir:
            report_file = Path(temp_dir) / "report.json"
            cmd = child_command(report_file, "Failure message", True, 1)
            rc, _output, errors = run(expect_refusal.main, [
                "--report", str(report_file),
                "--expect", "Failure message",
                "--", *cmd,
            ])
            self.assertEqual(rc, 1)
            self.assertIn("FAIL:", errors)

    def test_negative_stale_report_is_deleted_before_run(self):
        """A report left by an earlier run is deleted before the command starts."""
        with tempfile.TemporaryDirectory() as temp_dir:
            report_file = Path(temp_dir) / "report.json"
            report_file.write_text(json.dumps({"failure": "Old failure", "passed": False}), encoding="utf-8")
            cmd = no_report_command(1)
            rc, _output, errors = run(expect_refusal.main, [
                "--report", str(report_file),
                "--expect", "Old failure",
                "--", *cmd,
            ])
            self.assertEqual(rc, 1)
            self.assertIn("FAIL:", errors)

    def test_boundary_empty_expect_refused(self):
        """An empty expected text is refused, since it would match any failure."""
        with tempfile.TemporaryDirectory() as temp_dir:
            report_file = Path(temp_dir) / "report.json"
            cmd = child_command(report_file, "Failure message", False, 1)
            rc, _output, errors = run(expect_refusal.main, [
                "--report", str(report_file),
                "--expect", "",
                "--", *cmd,
            ])
            self.assertEqual(rc, 1)
            self.assertIn("FAIL:", errors)


if __name__ == "__main__":
    unittest.main()
