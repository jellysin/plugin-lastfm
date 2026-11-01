"""Verify acceptance criteria document validation rules and CLI tool."""

import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

REPO_ROOT = Path(__file__).resolve().parents[2]
if str(REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(REPO_ROOT))

from tools.acceptance import MAX_LINES, MAX_TOKENS_PER_ROW, ROWS, check, main


class AcceptanceTests(unittest.TestCase):
    def setUp(self):
        """Initialize temporary directory root for test isolation."""
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def make_valid_fixture(self) -> str:
        """Create valid fixture files and return acceptance markdown content."""
        dummy_code = self.root / "tests" / "dummy.cs"
        dummy_code.parent.mkdir(parents=True, exist_ok=True)
        dummy_code.write_text("class Dummy { void TestMethod() {} }", encoding="utf-8")

        dummy_doc = self.root / "docs" / "validation.md"
        dummy_doc.parent.mkdir(parents=True, exist_ok=True)
        dummy_doc.write_text("# Validation\n\n## The Run\n", encoding="utf-8")

        (self.root / "docs" / "acceptance.md").write_text("# Self\n", encoding="utf-8")

        lines = [
            "# Acceptance criteria",
            "",
            "| Row | Criterion | Evidence | Gap |",
            "| :--- | :--- | :--- | :--- |",
        ]
        for r in ROWS:
            if r == "O4":
                lines.append(f"| {r} | Criterion for {r} | | Gap explanation |")
            elif r == "O5":
                lines.append(f"| {r} | Criterion for {r} | `docs/acceptance.md` | |")
            elif r == "O6":
                lines.append(f"| {r} | Criterion for {r} | `docs/validation.md#the-run` | |")
            else:
                lines.append(f"| {r} | Criterion for {r} | `tests/dummy.cs::TestMethod` | |")
        return "\n".join(lines) + "\n"

    def test_committed_acceptance_passes(self):
        """Ensure default committed acceptance criteria passes verification."""
        self.assertEqual(0, main([]))

    def test_planted_missing_path(self):
        """Ensure non-existent evidence path is reported on check and CLI."""
        text = self.make_valid_fixture().replace("tests/dummy.cs", "tests/missing.cs")
        doc_path = self.root / "docs" / "acceptance.md"
        doc_path.write_text(text, encoding="utf-8")
        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr):
            code = main([str(doc_path), "--root", str(self.root)])
        self.assertEqual(1, code)
        errors = check(text, self.root)
        self.assertTrue(any("tests/missing.cs" in err for err in errors))
        self.assertIn("tests/missing.cs", stderr.getvalue())

    def test_planted_symbol_absent(self):
        """Ensure missing method declaration in evidence is detected."""
        text = self.make_valid_fixture().replace("::TestMethod", "::NoSuchMethod")
        errors = check(text, self.root)
        self.assertTrue(any("NoSuchMethod" in err for err in errors))

    def test_planted_symbol_only_in_comment(self):
        """Ensure symbol appearing only in comment is refused."""
        dummy_code = self.root / "tests" / "dummy.cs"
        text = self.make_valid_fixture()
        dummy_code.write_text("class Dummy { // TestMethod\n }", encoding="utf-8")
        errors = check(text, self.root)
        self.assertTrue(any("symbol 'TestMethod' not found" in err for err in errors))

    def test_planted_symbol_only_in_string(self):
        """Ensure symbol appearing only in string literal is refused."""
        dummy_code = self.root / "tests" / "dummy.cs"
        text = self.make_valid_fixture()
        dummy_code.write_text('class Dummy { string s = "TestMethod"; }', encoding="utf-8")
        errors = check(text, self.root)
        self.assertTrue(any("symbol 'TestMethod' not found" in err for err in errors))

    def test_planted_symbol_prefix_only(self):
        """Ensure symbol appearing only as prefix of identifier is refused."""
        dummy_code = self.root / "tests" / "dummy.cs"
        text = self.make_valid_fixture()
        dummy_code.write_text("class Dummy { void NotTestMethod() {} void TestMethodExtra() {} }", encoding="utf-8")
        errors = check(text, self.root)
        self.assertTrue(any("symbol 'TestMethod' not found" in err for err in errors))

    def test_planted_symbol_empty_name(self):
        """Ensure empty symbol name after :: is refused."""
        text = self.make_valid_fixture().replace("::TestMethod", "::")
        errors = check(text, self.root)
        self.assertTrue(any("empty test name" in err for err in errors))

    def test_planted_symbol_unsupported_suffix(self):
        """Ensure test name on unsupported file suffix is refused."""
        text = self.make_valid_fixture().replace(
            "tests/dummy.cs::TestMethod", "docs/validation.md::TestMethod"
        )
        errors = check(text, self.root)
        expected = "test names are only checked in .cs, .py, .mjs, .js, .ts files"
        self.assertTrue(any(expected in err for err in errors))

    def test_python_symbol_declaration(self):
        """Ensure Python def and async def match declaration syntax."""
        text = self.make_valid_fixture()
        py_code = self.root / "tests" / "dummy.py"
        py_code.write_text("def TestMethod():\n    pass\n", encoding="utf-8")
        text = text.replace("tests/dummy.cs::TestMethod", "tests/dummy.py::TestMethod")
        self.assertEqual([], check(text, self.root))

    def test_js_symbol_declaration(self):
        """Ensure JS/TS test('title', ...) matches exact title string."""
        text = self.make_valid_fixture()
        js_code = self.root / "tests" / "dummy.mjs"
        js_code.write_text("test('TestMethod', () => {});\n", encoding="utf-8")
        text = text.replace("tests/dummy.cs::TestMethod", "tests/dummy.mjs::TestMethod")
        self.assertEqual([], check(text, self.root))

    def test_planted_file_lines_cap_exceeded(self):
        """Ensure file exceeding MAX_LINES returns error rather than truncation."""
        extra_lines = ["<!-- padding -->"] * (MAX_LINES + 5)
        text = self.make_valid_fixture() + "\n" + "\n".join(extra_lines)
        errors = check(text, self.root)
        self.assertTrue(any(f"File exceeds {MAX_LINES} lines limit" in err for err in errors))

    def test_planted_evidence_tokens_cap_exceeded(self):
        """Ensure row exceeding MAX_TOKENS_PER_ROW returns error rather than truncation."""
        tokens = " ".join("`tests/dummy.cs::TestMethod`" for _ in range(MAX_TOKENS_PER_ROW + 2))
        lines = [
            (f"| D1 | Criterion | {tokens} | |" if l.startswith("| D1 |") else l)
            for l in self.make_valid_fixture().splitlines()
        ]
        errors = check("\n".join(lines) + "\n", self.root)
        self.assertTrue(any(f"evidence tokens count exceeds limit of {MAX_TOKENS_PER_ROW}" in err for err in errors))

    def test_planted_empty_criterion_error(self):
        """Ensure empty or whitespace Criterion cell is refused."""
        lines = [
            ("| D1 |    | `tests/dummy.cs::TestMethod` | |" if l.startswith("| D1 |") else l)
            for l in self.make_valid_fixture().splitlines()
        ]
        errors = check("\n".join(lines) + "\n", self.root)
        self.assertTrue(any("Criterion must not be empty" in err or "empty Criterion" in err for err in errors))

    def test_evidence_only_separators_empty_gap_error(self):
        """Ensure evidence cell with only separators and empty gap returns error."""
        lines = [
            ("| D1 | Criterion |  ; ;   | |" if l.startswith("| D1 |") else l)
            for l in self.make_valid_fixture().splitlines()
        ]
        errors = check("\n".join(lines) + "\n", self.root)
        self.assertTrue(any("needs evidence or a gap" in err for err in errors))

    def test_fenced_code_block_strips_rows(self):
        """Ensure table rows inside fenced code blocks are ignored as table structure."""
        text = self.make_valid_fixture()
        lines = text.splitlines()
        wrapped = ["```"] + [l for l in lines if l.startswith("| D1 |")] + ["```"]
        remaining = [l for l in lines if not l.startswith("| D1 |")]
        modified = "\n".join(remaining + wrapped) + "\n"
        errors = check(modified, self.root)
        self.assertTrue(any("Missing row: D1" in err for err in errors))

    def test_fenced_code_block_strips_headings(self):
        """Ensure headings inside fenced code blocks are ignored during anchor checking."""
        text = self.make_valid_fixture()
        dummy_doc = self.root / "docs" / "validation.md"
        dummy_doc.write_text("# Validation\n\n```\n## The Run\n```\n", encoding="utf-8")
        errors = check(text, self.root)
        self.assertTrue(any("anchor 'the-run' not found" in err for err in errors))

    def test_github_duplicate_heading_slugs(self):
        """Ensure duplicate heading slugs append -1, -2 disambiguation."""
        text = self.make_valid_fixture().replace(
            "docs/validation.md#the-run", "docs/validation.md#the-run-1"
        )
        dummy_doc = self.root / "docs" / "validation.md"
        dummy_doc.write_text("# Validation\n\n## The Run\n\n## The Run\n", encoding="utf-8")
        self.assertEqual([], check(text, self.root))

        dummy_doc.write_text("# Validation\n\n## The Run\n", encoding="utf-8")
        errors = check(text, self.root)
        self.assertTrue(any("anchor 'the-run-1' not found" in err for err in errors))

        text_3rd = self.make_valid_fixture().replace(
            "docs/validation.md#the-run", "docs/validation.md#the-run-2"
        )
        dummy_doc.write_text("# Validation\n\n## The Run\n\n## The Run\n\n## The Run\n", encoding="utf-8")
        self.assertEqual([], check(text_3rd, self.root))

    def test_planted_missing_row_one_error(self):
        """Ensure missing row triggers single missing row error."""
        text = self.make_valid_fixture()
        lines = [line for line in text.splitlines() if not line.startswith("| D2 |")]
        errors = check("\n".join(lines) + "\n", self.root)
        self.assertEqual(1, len(errors))
        self.assertIn("Missing row: D2", errors[0])

    def test_planted_duplicate_row_one_error(self):
        """Ensure duplicated row triggers single duplicate row error."""
        text = self.make_valid_fixture()
        lines = text.splitlines()
        for idx, line in enumerate(lines):
            if line.startswith("| D1 |"):
                lines.insert(idx + 1, line)
                break
        errors = check("\n".join(lines) + "\n", self.root)
        self.assertEqual(1, len(errors))
        self.assertIn("Duplicate row: D1", errors[0])

    def test_planted_unknown_row_one_error(self):
        """Ensure unknown row identifier is reported."""
        text = self.make_valid_fixture()
        lines = text.splitlines()
        lines.append("| X9 | Criterion | `tests/dummy.cs` | |")
        errors = check("\n".join(lines) + "\n", self.root)
        self.assertEqual(1, len(errors))
        self.assertIn("Unknown row: X9", errors[0])

    def test_planted_wrong_order_one_error(self):
        """Ensure disordered rows trigger order mismatch error."""
        text = self.make_valid_fixture()
        lines = text.splitlines()
        d1_idx = next(i for i, l in enumerate(lines) if l.startswith("| D1 |"))
        d2_idx = next(i for i, l in enumerate(lines) if l.startswith("| D2 |"))
        lines[d1_idx], lines[d2_idx] = lines[d2_idx], lines[d1_idx]
        errors = check("\n".join(lines) + "\n", self.root)
        self.assertEqual(1, len(errors))
        self.assertIn("Rows out of order", errors[0])

    def test_empty_evidence_with_empty_gap_error(self):
        """Ensure row with empty evidence and empty gap is refused."""
        text = self.make_valid_fixture()
        lines = [
            ("| D1 | Criterion | | |" if l.startswith("| D1 |") else l)
            for l in text.splitlines()
        ]
        errors = check("\n".join(lines) + "\n", self.root)
        self.assertTrue(any("needs evidence or a gap" in err for err in errors))

    def test_empty_evidence_with_gap_boundary_passes(self):
        """Ensure row with empty evidence but valid gap explanation passes."""
        text = self.make_valid_fixture()
        errors = check(text, self.root)
        self.assertEqual([], errors)

    def test_parent_traversal_path_error(self):
        """Ensure relative path traversing above root is rejected."""
        text = self.make_valid_fixture().replace("tests/dummy.cs", "../dummy.cs")
        errors = check(text, self.root)
        self.assertTrue(any("must not contain '..'" in err for err in errors))

    def test_absolute_path_error(self):
        """Ensure absolute paths are rejected even within repository."""
        abs_path = str(self.root.resolve() / "tests/dummy.cs")
        text = self.make_valid_fixture().replace("tests/dummy.cs", abs_path)
        errors = check(text, self.root)
        self.assertTrue(any("must not be absolute" in err for err in errors))

    def test_bad_anchor_error(self):
        """Ensure reference to non-existent markdown heading anchor fails."""
        text = self.make_valid_fixture().replace(
            "docs/validation.md#the-run", "docs/validation.md#bad-anchor"
        )
        errors = check(text, self.root)
        self.assertTrue(any("anchor 'bad-anchor' not found" in err for err in errors))

    RUN = "https://github.com/jellysin/plugin-lastfm/actions/runs/1/job/2#step:3:1"

    def test_run_link_counts_as_evidence(self):
        """Ensure GitHub actions job step link is accepted as evidence."""
        text = self.make_valid_fixture().replace(
            "| O4 | Criterion for O4 | | Gap explanation |", f"| O4 | Criterion for O4 | [CI run]({self.RUN}) | |"
        )
        self.assertEqual([], check(text, self.root))

    def test_link_outside_this_repository_actions_is_refused(self):
        """Ensure links outside target repository actions are refused."""
        foreign = "https://github.com/other/repo/actions/runs/1/job/2#step:3:1"
        text = self.make_valid_fixture().replace(
            "| O4 | Criterion for O4 | | Gap explanation |", f"| O4 | Criterion for O4 | [CI run]({foreign}) | |"
        )
        self.assertTrue(any("neither a path nor a run link" in err for err in check(text, self.root)))

    def test_bare_prose_evidence_is_refused(self):
        """Ensure unformatted prose in evidence cell is rejected."""
        text = self.make_valid_fixture().replace(
            "| O4 | Criterion for O4 | | Gap explanation |", "| O4 | Criterion for O4 | checked by hand | |"
        )
        self.assertTrue(any("neither a path nor a run link" in err for err in check(text, self.root)))

    def _mock_job_response(
        self,
        run_id=1,
        conclusion="success",
        step_conclusion="success",
        step_num=3,
        status=200,
        reason="OK",
    ):
        """Create mock HTTPResponse with status, reason and JSON payload."""
        payload = {
            "run_id": run_id,
            "conclusion": conclusion,
            "steps": [{"number": step_num, "conclusion": step_conclusion}],
        }
        mock_resp = MagicMock()
        mock_resp.status = status
        mock_resp.reason = reason
        mock_resp.read.return_value = json.dumps(payload).encode("utf-8")
        return mock_resp

    def _mock_connection(self, mock_resp=None, side_effect=None):
        """Create mock HTTPSConnection with getresponse, request and close."""
        mock_conn = MagicMock()
        if side_effect is not None:
            mock_conn.request.side_effect = side_effect
            mock_conn.getresponse.side_effect = side_effect
        elif mock_resp is not None:
            mock_conn.getresponse.return_value = mock_resp
        return mock_conn

    def test_check_links_good_job_passes(self):
        """Ensure --check-links passes when job and step succeed."""
        text = self.make_valid_fixture().replace(
            "| O4 | Criterion for O4 | | Gap explanation |", f"| O4 | Criterion for O4 | [CI run]({self.RUN}) | |"
        )
        mock_conn = self._mock_connection(self._mock_job_response())
        with patch("http.client.HTTPSConnection", return_value=mock_conn):
            errors = check(text, self.root, check_links=True)
        self.assertEqual([], errors)
        mock_conn.close.assert_called_once()

    def test_check_links_wrong_run_id_refused(self):
        """Ensure --check-links refuses mismatching run_id."""
        text = self.make_valid_fixture().replace(
            "| O4 | Criterion for O4 | | Gap explanation |", f"| O4 | Criterion for O4 | [CI run]({self.RUN}) | |"
        )
        mock_conn = self._mock_connection(self._mock_job_response(run_id=999))
        with patch("http.client.HTTPSConnection", return_value=mock_conn):
            errors = check(text, self.root, check_links=True)
        self.assertTrue(any("run_id mismatch" in err for err in errors))
        mock_conn.close.assert_called_once()

    def test_check_links_failed_job_refused(self):
        """Ensure --check-links refuses job with failing conclusion."""
        text = self.make_valid_fixture().replace(
            "| O4 | Criterion for O4 | | Gap explanation |", f"| O4 | Criterion for O4 | [CI run]({self.RUN}) | |"
        )
        mock_conn = self._mock_connection(self._mock_job_response(conclusion="failure"))
        with patch("http.client.HTTPSConnection", return_value=mock_conn):
            errors = check(text, self.root, check_links=True)
        self.assertTrue(any("job conclusion is 'failure'" in err for err in errors))
        mock_conn.close.assert_called_once()

    def test_check_links_failed_step_refused(self):
        """Ensure --check-links refuses job with failing step."""
        text = self.make_valid_fixture().replace(
            "| O4 | Criterion for O4 | | Gap explanation |", f"| O4 | Criterion for O4 | [CI run]({self.RUN}) | |"
        )
        mock_conn = self._mock_connection(self._mock_job_response(step_conclusion="failure"))
        with patch("http.client.HTTPSConnection", return_value=mock_conn):
            errors = check(text, self.root, check_links=True)
        self.assertTrue(any("step 3" in err for err in errors))
        mock_conn.close.assert_called_once()

    def test_check_links_missing_step_refused(self):
        """Ensure --check-links refuses job when step number is absent."""
        text = self.make_valid_fixture().replace(
            "| O4 | Criterion for O4 | | Gap explanation |", f"| O4 | Criterion for O4 | [CI run]({self.RUN}) | |"
        )
        mock_conn = self._mock_connection(self._mock_job_response(step_num=99))
        with patch("http.client.HTTPSConnection", return_value=mock_conn):
            errors = check(text, self.root, check_links=True)
        self.assertTrue(any("step 3" in err for err in errors))
        mock_conn.close.assert_called_once()

    def test_check_links_non_200_refused(self):
        """Ensure --check-links reports HTTP error on 404 status."""
        text = self.make_valid_fixture().replace(
            "| O4 | Criterion for O4 | | Gap explanation |", f"| O4 | Criterion for O4 | [CI run]({self.RUN}) | |"
        )
        mock_conn = self._mock_connection(self._mock_job_response(status=404, reason="Not Found"))
        with patch("http.client.HTTPSConnection", return_value=mock_conn):
            errors = check(text, self.root, check_links=True)
        self.assertTrue(any("HTTP error: 404 Not Found" in err for err in errors))
        self.assertTrue(any(self.RUN in err for err in errors))
        mock_conn.close.assert_called_once()

    def test_check_links_os_error_refused(self):
        """Ensure --check-links reports network error on OSError."""
        text = self.make_valid_fixture().replace(
            "| O4 | Criterion for O4 | | Gap explanation |", f"| O4 | Criterion for O4 | [CI run]({self.RUN}) | |"
        )
        mock_conn = self._mock_connection(side_effect=OSError("network unreachable"))
        with patch("http.client.HTTPSConnection", return_value=mock_conn):
            errors = check(text, self.root, check_links=True)
        self.assertTrue(any("network error: OSError" in err for err in errors))
        self.assertTrue(any(self.RUN in err for err in errors))
        mock_conn.close.assert_called_once()

    def test_check_links_auth_header_sent(self):
        """Ensure Authorization Bearer is sent when GITHUB_TOKEN is present."""
        text = self.make_valid_fixture().replace(
            "| O4 | Criterion for O4 | | Gap explanation |", f"| O4 | Criterion for O4 | [CI run]({self.RUN}) | |"
        )
        mock_conn = self._mock_connection(self._mock_job_response())
        fake_value = "x" * 8
        with (
            patch.dict("os.environ", {"GITHUB_TOKEN": fake_value}),
            patch("http.client.HTTPSConnection", return_value=mock_conn),
        ):
            errors = check(text, self.root, check_links=True)
        self.assertEqual([], errors)
        mock_conn.close.assert_called_once()
        mock_conn.request.assert_called_once()
        call_kwargs = mock_conn.request.call_args.kwargs
        headers = call_kwargs.get("headers", {})
        self.assertEqual(f"Bearer {fake_value}", headers.get("Authorization"))
        for err in errors:
            self.assertNotIn(fake_value, err)

        mock_conn_err = self._mock_connection(self._mock_job_response(status=401, reason="Unauthorized"))
        with (
            patch.dict("os.environ", {"GITHUB_TOKEN": fake_value}),
            patch("http.client.HTTPSConnection", return_value=mock_conn_err),
        ):
            err_list = check(text, self.root, check_links=True)
        self.assertTrue(len(err_list) > 0)
        for err in err_list:
            self.assertNotIn(fake_value, err)

    def test_check_links_cli_failure_exit_1(self):
        """Ensure main CLI with --check-links exits 1 on link failure."""
        text = self.make_valid_fixture().replace(
            "| O4 | Criterion for O4 | | Gap explanation |", f"| O4 | Criterion for O4 | [CI run]({self.RUN}) | |"
        )
        doc_path = self.root / "docs" / "acceptance.md"
        doc_path.write_text(text, encoding="utf-8")
        mock_conn = self._mock_connection(self._mock_job_response(status=500, reason="Server Error"))
        stderr = io.StringIO()
        with (
            patch("http.client.HTTPSConnection", return_value=mock_conn),
            contextlib.redirect_stderr(stderr),
        ):
            code = main([str(doc_path), "--root", str(self.root), "--check-links"])
        self.assertEqual(1, code)
        self.assertIn(self.RUN, stderr.getvalue())
        self.assertIn("500 Server Error", stderr.getvalue())
        mock_conn.close.assert_called_once()


if __name__ == "__main__":
    unittest.main()
