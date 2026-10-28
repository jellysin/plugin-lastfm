"""The dependency audit refuses incomplete runs and plans exactly one issue action."""

import contextlib
import io
import json
import subprocess
import unittest
from unittest.mock import patch

import console
from tools import dependency_audit
from tools.dependency_audit import MARKER, TITLE, AuditError, Finding, audit, forge, main, plan, render, tracked

REPOSITORY = "example/project"
ADVISORY = "https://github.com/advisories/GHSA-6j4f-fj2g-mc7p"
VULNERABLE = {"severity": "high", "range": "4.0.0 - 5.0.11", "via": [{"url": ADVISORY}], "fixAvailable": True}
FINDING = Finding("brace-expansion", "high", "4.0.0 - 5.0.11", (ADVISORY,), "yes")


def completed(stdout, returncode=0, stderr=""):
    return subprocess.CompletedProcess([], returncode, stdout, stderr)


def npm_report(vulnerabilities):
    return json.dumps({"auditReportVersion": 2, "vulnerabilities": vulnerabilities, "metadata": {"vulnerabilities": {"total": len(vulnerabilities)}}})


def audited(stdout, returncode):
    with patch.object(dependency_audit.subprocess, "run", return_value=completed(stdout, returncode)) as run:
        return audit("."), run.call_args.args[0]


class AuditTests(unittest.TestCase):
    def test_reads_findings_from_a_complete_report(self):
        found, command = audited(npm_report({"brace-expansion": VULNERABLE}), 1)
        self.assertEqual([FINDING], found)
        self.assertEqual(["npm", "audit", "--json", "--package-lock-only"], command)

    def test_clean_report_has_no_findings(self):
        self.assertEqual([], audited(npm_report({}), 0)[0])

    def test_refuses_runs_that_did_not_complete(self):
        refused = {
            "npm error": (json.dumps({"error": {"code": "ENOLOCK"}}), 1),
            "registry failure": (json.dumps({"message": "request failed", "error": {"summary": ""}}), 1),
            "error beside an empty result": (json.dumps({"error": {"code": "E500"}, "vulnerabilities": {}, "metadata": {"vulnerabilities": {"total": 0}}}), 0),
            "no report": ("", 1),
            "not an object": ("[]", 0),
            "findings with a clean exit": (npm_report({"brace-expansion": VULNERABLE}), 0),
            "failing exit without findings": (npm_report({}), 1),
            "count contradicts the list": (json.dumps({"vulnerabilities": {}, "metadata": {"vulnerabilities": {"total": 2}}}), 0),
        }
        for label, (stdout, returncode) in refused.items():
            with self.subTest(label), self.assertRaises(AuditError):
                audited(stdout, returncode)

    def test_transitive_finding_names_its_path_and_major_fix(self):
        entry = {"severity": "high", "range": ">=10.0.0", "via": ["brace-expansion"], "fixAvailable": {"name": "eslint", "version": "11.0.0", "isSemVerMajor": True}}
        (found,) = audited(npm_report({"minimatch": entry}), 1)[0]
        self.assertEqual((("through `brace-expansion`",), "eslint 11.0.0 (major)"), (found.sources, found.fix))


class RenderTests(unittest.TestCase):
    def test_body_starts_with_the_marker_and_lists_the_finding(self):
        body = render([FINDING], "0123abc")
        self.assertTrue(body.startswith(MARKER))
        self.assertIn(f"| `brace-expansion` | high | `4.0.0 - 5.0.11` | {ADVISORY} | yes |", body)

    def test_registry_text_cannot_inject_markup_or_links(self):
        hostile = {"severity": "high](https://evil.example)", "range": "1 || 2 `|", "via": [{"url": "https://evil.example/x"}], "fixAvailable": False}
        with patch.object(dependency_audit.subprocess, "run", return_value=completed(npm_report({"a`|\n[x](y)": hostile}), 1)):
            body = render(audit("."), "main\n| injected")
        self.assertNotIn("evil.example/x", body)
        self.assertNotIn("](", body)
        self.assertIn("| `axy` | unknown | `1 or 2 ` | not stated | no |", body)
        self.assertEqual(9, len(body.splitlines()))

    def test_table_is_bounded(self):
        body = render([FINDING] * (dependency_audit.MAX_FINDINGS + 3), "0123abc")
        self.assertEqual(dependency_audit.MAX_FINDINGS, body.count("| `brace-expansion` |"))
        self.assertIn("| 3 more |", body)


class IssueTests(unittest.TestCase):
    def test_opens_one_labelled_issue_for_the_first_finding(self):
        arguments, body = plan([FINDING], None, REPOSITORY, "0123abc")
        self.assertEqual(["issue", "create", "--repo", REPOSITORY, "--title", TITLE, "--label", "dependencies", "--body-file", "-"], arguments)
        self.assertTrue(body.startswith(MARKER))

    def test_updates_the_open_issue_instead_of_opening_another(self):
        arguments, body = plan([FINDING], 41, REPOSITORY, "0123abc")
        self.assertEqual(["issue", "edit", "41", "--repo", REPOSITORY, "--body-file", "-"], arguments)
        self.assertIn("brace-expansion", body)

    def test_clean_audit_closes_the_open_issue_and_otherwise_does_nothing(self):
        arguments, body = plan([], 41, REPOSITORY, "0123abc")
        self.assertEqual(["issue", "close", "41", "--repo", REPOSITORY, "--reason", "completed", "--comment", "The audit is clean at `0123abc`."], arguments)
        self.assertIsNone(body)
        self.assertIsNone(plan([], None, REPOSITORY, "0123abc"))

    def test_owns_only_an_issue_with_its_title_and_marker(self):
        listed = [
            {"number": 7, "title": TITLE, "body": "written by a person"},
            {"number": 8, "title": "Another issue", "body": MARKER},
            {"number": 12, "title": TITLE, "body": MARKER + "\nolder run"},
            {"number": 9, "title": TITLE, "body": MARKER + "\nolder run"},
        ]
        with patch.object(dependency_audit.subprocess, "run", return_value=completed(json.dumps(listed))) as run:
            self.assertEqual(9, tracked(REPOSITORY))
        self.assertEqual(["gh", "issue", "list", "--repo", REPOSITORY, "--state", "open"], run.call_args.args[0][:7])
        with patch.object(dependency_audit.subprocess, "run", return_value=completed(json.dumps(listed[:2]))):
            self.assertIsNone(tracked(REPOSITORY))

    def test_refused_forge_call_names_the_missing_permission(self):
        denied = completed("", 1, "HTTP 403: Resource not accessible by integration")
        with patch.object(dependency_audit.subprocess, "run", return_value=denied), self.assertRaisesRegex(AuditError, "issues: write.*HTTP 403"):
            forge(["issue", "create"], "body")


class MainTests(unittest.TestCase):
    def run_main(self, arguments, found, forge_result):
        calls = []

        def fake_forge(arguments, text=None):
            calls.append(arguments[:2])
            if isinstance(forge_result, Exception) and arguments[1] != "list":
                raise forge_result
            return "[]" if arguments[1] == "list" else "https://example.invalid/issues/1\n"

        with patch.object(dependency_audit, "audit", return_value=found), patch.object(dependency_audit, "forge", side_effect=fake_forge):
            status, output, errors = console.run(main, ["--repository", REPOSITORY, "--revision", "0123abc", *arguments])
        return status, calls, output, errors

    def test_findings_open_the_issue_and_fail_the_run(self):
        status, calls, output, _ = self.run_main([], [FINDING], None)
        self.assertEqual((1, [["issue", "list"], ["issue", "create"]]), (status, calls))
        self.assertIn("1 vulnerable package(s)", output)

    def test_dry_run_prints_the_issue_and_writes_nothing(self):
        status, calls, output, _ = self.run_main(["--dry-run"], [FINDING], None)
        self.assertEqual((1, [["issue", "list"]]), (status, calls))
        self.assertIn("Dry run, would run: gh issue create", output)
        self.assertIn("| `brace-expansion` | high |", output)

    def test_clean_audit_passes_without_touching_the_tracker(self):
        self.assertEqual((0, [["issue", "list"]]), self.run_main([], [], None)[:2])

    def test_missing_permission_fails_differently_from_findings(self):
        status, _, _, errors = self.run_main([], [FINDING], AuditError("gh issue create failed; the token needs `issues: write`"))
        self.assertEqual(2, status)
        self.assertIn("issues: write", errors)

    def test_requires_a_repository(self):
        with patch.dict(dependency_audit.os.environ, {}, clear=True), contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(2, main([]))


if __name__ == "__main__":
    unittest.main()
