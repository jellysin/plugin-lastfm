"""Verify acceptance criteria document validation rules and CLI tool."""

import contextlib
import io
import sys
import tempfile
import unittest
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
if str(REPO_ROOT) not in sys.path:
    sys.path.insert(0, str(REPO_ROOT))

from tools.acceptance import ROWS, check, main


class AcceptanceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def make_valid_fixture(self) -> str:
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
        self.assertEqual(0, main([]))

    def test_planted_missing_path(self):
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
        text = self.make_valid_fixture().replace("::TestMethod", "::NoSuchMethod")
        errors = check(text, self.root)
        self.assertTrue(any("NoSuchMethod" in err for err in errors))

    def test_planted_missing_row_one_error(self):
        text = self.make_valid_fixture()
        lines = [line for line in text.splitlines() if not line.startswith("| D2 |")]
        errors = check("\n".join(lines) + "\n", self.root)
        self.assertEqual(1, len(errors))
        self.assertIn("Missing row: D2", errors[0])

    def test_planted_duplicate_row_one_error(self):
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
        text = self.make_valid_fixture()
        lines = text.splitlines()
        lines.append("| X9 | Criterion | `tests/dummy.cs` | |")
        errors = check("\n".join(lines) + "\n", self.root)
        self.assertEqual(1, len(errors))
        self.assertIn("Unknown row: X9", errors[0])

    def test_planted_wrong_order_one_error(self):
        text = self.make_valid_fixture()
        lines = text.splitlines()
        d1_idx = next(i for i, l in enumerate(lines) if l.startswith("| D1 |"))
        d2_idx = next(i for i, l in enumerate(lines) if l.startswith("| D2 |"))
        lines[d1_idx], lines[d2_idx] = lines[d2_idx], lines[d1_idx]
        errors = check("\n".join(lines) + "\n", self.root)
        self.assertEqual(1, len(errors))
        self.assertIn("Rows out of order", errors[0])

    def test_empty_evidence_with_empty_gap_error(self):
        text = self.make_valid_fixture()
        lines = [
            ("| D1 | Criterion | | |" if l.startswith("| D1 |") else l)
            for l in text.splitlines()
        ]
        errors = check("\n".join(lines) + "\n", self.root)
        self.assertTrue(any("empty Evidence must have a non-empty Gap" in err for err in errors))

    def test_empty_evidence_with_gap_boundary_passes(self):
        text = self.make_valid_fixture()
        errors = check(text, self.root)
        self.assertEqual([], errors)

    def test_parent_traversal_path_error(self):
        text = self.make_valid_fixture().replace("tests/dummy.cs", "../dummy.cs")
        errors = check(text, self.root)
        self.assertTrue(any("must not contain '..'" in err for err in errors))

    def test_absolute_path_error(self):
        text = self.make_valid_fixture().replace("tests/dummy.cs", "/tmp/dummy.cs")
        errors = check(text, self.root)
        self.assertTrue(any("must not be absolute" in err for err in errors))

    def test_bad_anchor_error(self):
        text = self.make_valid_fixture().replace(
            "docs/validation.md#the-run", "docs/validation.md#bad-anchor"
        )
        errors = check(text, self.root)
        self.assertTrue(any("anchor 'bad-anchor' not found" in err for err in errors))


    RUN = "https://github.com/jellysin/plugin-lastfm/actions/runs/1/job/2#step:3:1"

    def test_run_link_counts_as_evidence(self):
        text = self.make_valid_fixture().replace(
            "| O4 | Criterion for O4 | | Gap explanation |", f"| O4 | Criterion for O4 | [CI run]({self.RUN}) | |"
        )
        self.assertEqual([], check(text, self.root))

    def test_link_outside_this_repository_actions_is_refused(self):
        foreign = "https://github.com/other/repo/actions/runs/1/job/2#step:3:1"
        text = self.make_valid_fixture().replace(
            "| O4 | Criterion for O4 | | Gap explanation |", f"| O4 | Criterion for O4 | [CI run]({foreign}) | |"
        )
        self.assertTrue(any("neither a path nor a run link" in err for err in check(text, self.root)))

    def test_bare_prose_evidence_is_refused(self):
        text = self.make_valid_fixture().replace(
            "| O4 | Criterion for O4 | | Gap explanation |", "| O4 | Criterion for O4 | checked by hand | |"
        )
        self.assertTrue(any("neither a path nor a run link" in err for err in check(text, self.root)))


if __name__ == "__main__":
    unittest.main()
