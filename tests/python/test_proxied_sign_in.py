"""A browser half that ended without a result is a failed run, not a crash, and an older result does not stand in for it."""

import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tests" / "integration"))
from proxied_sign_in import read_results  # noqa: E402

RESULTS = [{"name": "a scenario", "passed": False, "detail": "refused"}]


class ReadResultsTests(unittest.TestCase):
    def setUp(self):
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        self.path = Path(scratch.name) / "result.json"

    def read(self, status):
        said = io.StringIO()
        with contextlib.redirect_stdout(said):
            return read_results(self.path, status), said.getvalue()

    def test_a_written_result_is_read_whether_the_scenarios_passed_or_not(self):
        self.path.write_text(json.dumps(RESULTS), encoding="utf-8")
        for status in (0, 1):
            with self.subTest(status):
                self.assertEqual((RESULTS, ""), self.read(status))

    def test_no_result_is_named_as_a_failure(self):
        # Node ends with status 1 on an error of its own, the same status as a failed scenario.
        for status in (0, 1, 13):
            with self.subTest(status):
                results, said = self.read(status)
                self.assertEqual([], results)
                self.assertEqual(f"FAIL: the browser half ended with status {status} and left no result.\n", said)

    def test_a_result_beside_another_status_is_not_read(self):
        self.path.write_text(json.dumps(RESULTS), encoding="utf-8")
        results, said = self.read(134)
        self.assertEqual([], results)
        self.assertIn("status 134", said)


if __name__ == "__main__":
    unittest.main()
