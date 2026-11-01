"""The Semgrep gate reads one pinned release, scans only source suffixes and knows which planted lines to expect."""

import unittest
from unittest import mock

from tools import semgrep_gate
from tools.semgrep_gate import (
    GateError,
    expected_lines,
    launcher,
    pinned_version,
    scanned,
)


class PinTests(unittest.TestCase):
    def test_reads_the_single_pin(self):
        self.assertEqual(pinned_version("# comment\nsemgrep==1.183.0\n"), "1.183.0")

    def test_refuses_a_missing_pin(self):
        with self.assertRaises(GateError):
            pinned_version("semgrep>=1.0\n")

    def test_refuses_two_pins(self):
        with self.assertRaises(GateError):
            pinned_version("semgrep==1.0.0\nsemgrep==1.1.0\n")

    def test_committed_requirements_pin_one_release(self):
        self.assertRegex(pinned_version(semgrep_gate.REQUIREMENTS.read_text(encoding="utf-8")), r"^\d+\.\d+\.\d+$")


class TargetTests(unittest.TestCase):
    def test_keeps_scanned_suffixes_in_order(self):
        paths = ["web/a.mjs", "README.md", "src/B.cs", "tools/c.py", ".github/workflows/ci.yml", "LICENSE"]
        self.assertEqual(scanned(paths), ["web/a.mjs", "src/B.cs", "tools/c.py", ".github/workflows/ci.yml"])

    def test_empty_list_scans_nothing(self):
        self.assertEqual(scanned([]), [])

    def test_refuses_a_path_outside_the_repository(self):
        for path in ("../outside.py", "/etc/passwd.py", "src/../../outside.cs"):
            with self.subTest(path=path), self.assertRaises(GateError):
                scanned(["tools/a.py", path])

    def test_keeps_a_dash_name_inside_the_repository(self):
        self.assertEqual(scanned(["-rf.py"]), ["-rf.py"])

    def test_refuses_more_than_the_bound(self):
        with self.assertRaises(GateError):
            scanned(["a.py"] * (semgrep_gate.MAX_TARGETS + 1))


class LauncherTests(unittest.TestCase):
    def test_prefers_uvx_and_pins_the_release(self):
        with mock.patch.object(semgrep_gate.shutil, "which", side_effect=lambda name: f"/bin/{name}"):
            self.assertEqual(launcher("1.2.3")[:6], ["/bin/uvx", "--quiet", "--python", "3.14", "--from", "semgrep==1.2.3"])

    def test_falls_back_to_pipx_by_name(self):
        with mock.patch.object(semgrep_gate.shutil, "which", side_effect=lambda name: "/bin/pipx" if name == "pipx" else None):
            self.assertEqual(
                launcher("1.2.3")[:7],
                ["/bin/pipx", "run", "--python", "3.14", "--fetch-python=missing", "--spec", "semgrep==1.2.3"],
            )

    def test_fails_without_a_launcher(self):
        with mock.patch.object(semgrep_gate.shutil, "which", return_value=None), self.assertRaises(GateError):
            launcher("1.2.3")


class PlantedTests(unittest.TestCase):
    def test_marks_only_lines_ending_in_the_mark(self):
        self.assertEqual(expected_lines("a // planted\nb\nc planted  \nplanted d\n"), {1, 3})

    def test_every_planted_file_has_a_case_and_a_negative(self):
        for name, text in semgrep_gate.PLANTED.items():
            lines = text.splitlines()
            self.assertTrue(expected_lines(text), f"{name}: no planted line")
            self.assertLess(len(expected_lines(text)), len([line for line in lines if line.strip()]), name)

    def test_keeps_the_fixture_order_regex_from_pr_248(self):
        self.assertIn("(?:\\.[A-Za-z_$][\\w$]*|\\[[^\\]]+\\])+", semgrep_gate.FIXTURE_ORDER_REGEX)


if __name__ == "__main__":
    unittest.main()
