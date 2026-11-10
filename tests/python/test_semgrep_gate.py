"""The Semgrep gate reads one pinned release, scans only source suffixes and knows which planted lines to expect."""

import contextlib
from datetime import date
import io
import unittest
from unittest import mock

from tools import semgrep_gate
from tools.semgrep_gate import (
    AUDIT_RULE,
    EXCEPTIONS,
    Exemption,
    GateError,
    PACKS,
    ROOT,
    excepted,
    exception_problems,
    expected_lines,
    launcher,
    pinned_version,
    scanned,
)

EXPECTED_AUDIT_RULES = (
    "r/python.lang.security.audit.dangerous-subprocess-use-audit.dangerous-subprocess-use-audit",
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
                ["/bin/pipx", "run", "--python", "3.14", "--fetch-missing-python", "--spec", "semgrep==1.2.3"],
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


class ExceptionTests(unittest.TestCase):
    def test_accepts_before_expiry(self):
        """An exception is accepted before its expiry date."""
        exceptions = {("a.py", AUDIT_RULE): Exemption("2026-11-20", "C", "2026-11-10", "reason", (1,))}
        self.assertEqual(exception_problems(exceptions, date(2026, 11, 19)), [])

    def test_accepts_on_expiry_day(self):
        """An exception is accepted on its expiry date."""
        exceptions = {("a.py", AUDIT_RULE): Exemption("2026-11-20", "C", "2026-11-10", "reason", (1,))}
        self.assertEqual(exception_problems(exceptions, date(2026, 11, 20)), [])

    def test_refuses_the_day_after_expiry(self):
        """An exception is refused the day after its expiry date."""
        exceptions = {("a.py", AUDIT_RULE): Exemption("2026-11-20", "C", "2026-11-10", "reason", (1,))}
        problems = exception_problems(exceptions, date(2026, 11, 21))
        self.assertEqual(len(problems), 1)
        self.assertIn("expired on 2026-11-20", problems[0])

    def test_refuses_category_outside_a_to_d(self):
        """Category E outside A-D is refused."""
        exceptions = {("a.py", AUDIT_RULE): Exemption("2026-11-20", "E", "2026-11-10", "reason", (1,))}
        problems = exception_problems(exceptions, date(2026, 11, 10))
        self.assertEqual(len(problems), 1)
        self.assertIn("category 'E' outside A-D", problems[0])

    def test_refuses_unparseable_date(self):
        """An unparseable date in an exception entry is refused."""
        for invalid in ("not-a-date", "2026-13-40", "2026-02-29"):
            with self.subTest(invalid=invalid):
                exceptions = {("a.py", AUDIT_RULE): Exemption(invalid, "C", "2026-11-10", "reason", (1,))}
                problems = exception_problems(exceptions, date(2026, 11, 10))
                self.assertEqual(len(problems), 1)
                self.assertIn("unparseable", problems[0])

    def test_refuses_empty_reason(self):
        """An empty or whitespace reason is refused."""
        for reason in ("", "   "):
            with self.subTest(reason=reason):
                exceptions = {("a.py", AUDIT_RULE): Exemption("2026-11-20", "C", "2026-11-10", reason, (1,))}
                problems = exception_problems(exceptions, date(2026, 11, 10))
                self.assertEqual(len(problems), 1)
                self.assertIn("empty reason", problems[0])

    def test_committed_exceptions_give_no_problem(self):
        """The committed EXCEPTIONS give no problem on 2026-11-10."""
        self.assertEqual(exception_problems(EXCEPTIONS, date(2026, 11, 10)), [])

    def test_excepted_matches_relative_and_absolute_paths(self):
        """excepted is true for relative path and absolute path under ROOT."""
        rel_finding = {
            "path": ".config/lefthook/scripts/common.py",
            "check_id": AUDIT_RULE,
            "start": {"line": 281},
        }
        abs_finding = {
            "path": str(ROOT / ".config/lefthook/scripts/common.py"),
            "check_id": AUDIT_RULE,
            "start": {"line": 334},
        }
        self.assertTrue(excepted(rel_finding, EXCEPTIONS))
        self.assertTrue(excepted(abs_finding, EXCEPTIONS))

    def test_excepted_false_for_another_rule_or_file(self):
        """excepted is false for another rule on the file and the same rule on another file."""
        other_rule = {
            "path": ".config/lefthook/scripts/common.py",
            "check_id": "other.rule",
            "start": {"line": 281},
        }
        other_file = {
            "path": "tools/semgrep_gate.py",
            "check_id": AUDIT_RULE,
            "start": {"line": 281},
        }
        self.assertFalse(excepted(other_rule, EXCEPTIONS))
        self.assertFalse(excepted(other_file, EXCEPTIONS))

    def test_excepted_false_for_line_outside_lines(self):
        """excepted is false for an excepted file and rule on a line outside lines."""
        finding = {
            "path": ".config/lefthook/scripts/common.py",
            "check_id": AUDIT_RULE,
            "start": {"line": 282},
        }
        self.assertFalse(excepted(finding, EXCEPTIONS))

    def test_refuses_empty_lines(self):
        """exception_problems refuses an exception entry with empty lines."""
        exceptions = {("a.py", AUDIT_RULE): Exemption("2026-11-20", "C", "2026-11-10", "reason", ())}
        problems = exception_problems(exceptions, date(2026, 11, 10))
        self.assertEqual(problems, ["a.py: no lines"])

    def test_refuses_non_positive_line(self):
        """exception_problems refuses a line number that is not a positive integer."""
        exceptions = {("a.py", AUDIT_RULE): Exemption("2026-11-20", "C", "2026-11-10", "reason", (0,))}
        problems = exception_problems(exceptions, date(2026, 11, 10))
        self.assertEqual(problems, ["a.py: line 0 is not a positive integer"])


class MainTests(unittest.TestCase):
    def main_with(
        self,
        argv: list[str],
        today: date,
        findings: list[dict] | tuple = (),
        targets: tuple[str, ...] | list[str] = ("a.py",),
    ) -> tuple[int, mock.MagicMock, str]:
        """Run main with launcher, date, scanned, run and stderr patched; return status, the run mock and stderr."""
        stderr = io.StringIO()
        with contextlib.ExitStack() as stack:
            stack.enter_context(mock.patch.object(semgrep_gate, "launcher", return_value=["semgrep"]))
            mock_date = stack.enter_context(mock.patch.object(semgrep_gate, "date", wraps=date))
            mock_date.today.return_value = today
            stack.enter_context(mock.patch.object(semgrep_gate, "scanned", return_value=list(targets)))
            fake_run = stack.enter_context(mock.patch.object(semgrep_gate, "run", return_value=list(findings)))
            stack.enter_context(mock.patch("sys.stderr", stderr))
            status = semgrep_gate.main(argv)
            return status, fake_run, stderr.getvalue()

    def test_main_without_self_test_passes_packs_and_audit_rules(self):
        """main without --self-test passes PACKS + AUDIT_RULES as packs to run."""
        status, fake_run, _ = self.main_with(["a.py"], date(2026, 11, 10))
        self.assertEqual(status, 0)
        fake_run.assert_called_once()
        self.assertEqual(fake_run.call_args.args[2], PACKS + EXPECTED_AUDIT_RULES)

    def test_self_test_passes_audit_rules(self):
        """--self-test passes AUDIT_RULES as packs to run."""
        with mock.patch.object(semgrep_gate, "run", return_value=[]) as fake_run:
            semgrep_gate.self_test(["semgrep"])
            fake_run.assert_called_once()
            self.assertEqual(fake_run.call_args.args[2], EXPECTED_AUDIT_RULES)

    def test_main_fails_on_an_expired_exception(self):
        """main returns 1 and names the entry when an exception has expired."""
        status, fake_run, stderr = self.main_with([], date(2026, 11, 21))
        self.assertEqual(status, 1)
        fake_run.assert_not_called()
        self.assertIn(".config/lefthook/scripts/common.py", stderr)

    def test_main_drops_excepted_findings_and_reports_non_excepted(self):
        """main drops an excepted finding and still reports and returns 1 for a non-excepted one."""
        excepted_finding = {
            "path": ".config/lefthook/scripts/common.py",
            "check_id": AUDIT_RULE,
            "start": {"line": 281},
            "extra": {"message": "dangerous subprocess call"},
        }
        active_finding = {
            "path": "tools/other.py",
            "check_id": AUDIT_RULE,
            "start": {"line": 10},
            "extra": {"message": "dangerous subprocess call"},
        }
        status, _, output = self.main_with(
            ["tools/other.py"],
            date(2026, 11, 10),
            findings=[excepted_finding, active_finding],
            targets=["tools/other.py"],
        )
        self.assertEqual(status, 1)
        self.assertNotIn("common.py", output)
        self.assertIn("tools/other.py:10:", output)

    def test_self_test_fails_on_an_expired_exception(self):
        """main with --self-test returns 1 and never calls run when an exception has expired."""
        status, fake_run, stderr = self.main_with(["--self-test"], date(2026, 11, 21))
        self.assertEqual(status, 1)
        fake_run.assert_not_called()
        self.assertIn(".config/lefthook/scripts/common.py", stderr)

    def test_main_drops_excepted_line_and_reports_unexcepted_line(self):
        """main drops the common.py finding at line 281 but reports a common.py finding at line 300."""
        excepted_finding = {
            "path": ".config/lefthook/scripts/common.py",
            "check_id": AUDIT_RULE,
            "start": {"line": 281},
            "extra": {"message": "dangerous subprocess call"},
        }
        active_finding = {
            "path": ".config/lefthook/scripts/common.py",
            "check_id": AUDIT_RULE,
            "start": {"line": 300},
            "extra": {"message": "dangerous subprocess call"},
        }
        status, _, output = self.main_with(
            [".config/lefthook/scripts/common.py"],
            date(2026, 11, 10),
            findings=[excepted_finding, active_finding],
            targets=[".config/lefthook/scripts/common.py"],
        )
        self.assertEqual(status, 1)
        self.assertNotIn(":281:", output)
        self.assertIn(".config/lefthook/scripts/common.py:300:", output)

    def test_self_test_arguments_include_local_and_audit_rules_without_packs(self):
        """self_test invokes semgrep with local rules and audit rule but without registry packs."""
        completed = semgrep_gate.subprocess.CompletedProcess([], 0, '{"results": [], "errors": []}', "")
        with mock.patch.object(semgrep_gate.subprocess, "run", return_value=completed) as fake_sub_run:
            semgrep_gate.self_test(["semgrep"])
            fake_sub_run.assert_called()
            args = fake_sub_run.call_args.args[0]
            pairs = list(zip(args, args[1:]))
            self.assertIn(("--config", str(ROOT / ".config" / "semgrep")), pairs)
            self.assertIn(("--config", EXPECTED_AUDIT_RULES[0]), pairs)
            for pack in PACKS:
                self.assertNotIn(pack, args)


if __name__ == "__main__":
    unittest.main()
