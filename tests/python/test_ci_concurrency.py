"""Main CI runs must never cancel each other; pull request runs may."""

import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CI = ROOT / ".github" / "workflows" / "ci.yml"

EXPECTED = (
    "concurrency:\n"
    "  group: ci-${{ github.workflow }}-${{ github.event.pull_request.number || github.run_id }}\n"
    "  cancel-in-progress: ${{ github.event_name == 'pull_request' }}\n"
)


def concurrency_block(text):
    match = re.search(r"^concurrency:\n(?:  .*\n)+", text, re.MULTILINE)
    return match.group(0) if match else None


class CiConcurrencyTests(unittest.TestCase):
    def test_main_runs_have_their_own_group_and_never_cancel(self):
        self.assertEqual(concurrency_block(CI.read_text(encoding="utf-8")), EXPECTED)

    def test_refuses_the_settings_that_drop_main_results(self):
        planted = {
            "cancel always": EXPECTED.replace(
                "${{ github.event_name == 'pull_request' }}", "true"),
            "group per ref": EXPECTED.replace("github.run_id", "github.ref"),
            "no concurrency": None,
        }
        for name, block in planted.items():
            with self.subTest(name):
                text = "name: CI\n" + (block or "") + "jobs:\n"
                self.assertNotEqual(concurrency_block(text), EXPECTED)

    def test_only_one_concurrency_block(self):
        text = CI.read_text(encoding="utf-8")
        self.assertEqual(len(re.findall(r"^concurrency:", text, re.MULTILINE)), 1)


if __name__ == "__main__":
    unittest.main()
