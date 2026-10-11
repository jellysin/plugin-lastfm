"""Every workflow reference to the release helper names one commit and says which release it is."""

import re
import unittest
from pathlib import Path

WORKFLOWS = Path(__file__).resolve().parents[2] / ".github" / "workflows"
PIN = r"([0-9a-f]{40}) # (v\d+\.\d+\.\d+)$"
USED = re.compile(r"uses: jellysin/release-helper/[A-Za-z0-9_./-]+@(\S+)(.*)$", re.MULTILINE)
CHECKED_OUT = re.compile(r"repository: jellysin/release-helper\n\s+ref: (\S+)(.*)$", re.MULTILINE)
MENTION = re.compile(r"jellysin/release-helper")
EXPECTED = {"ci.yml": 3, "prepare-release.yml": 1, "release.yml": 3, "release-automation.yml": 1}


def pins(text):
    """Return (commit, release) for each helper reference, or None for one that is not a full commit with its release."""
    found = []
    for pattern in (USED, CHECKED_OUT):
        for reference, comment in pattern.findall(text):
            match = re.fullmatch(PIN, reference + comment)
            found.append(match.groups() if match else None)
    return found


class HelperPinTests(unittest.TestCase):
    def test_workflows_name_one_helper_commit_and_its_release(self):
        counted = {}
        for path in sorted(WORKFLOWS.glob("*.yml")):
            text = path.read_text(encoding="utf-8")
            found = pins(text)
            # A reference in a form the patterns do not read would escape the comparison.
            self.assertEqual(len(MENTION.findall(text)), len(found), path.name)
            if found:
                counted[path.name] = found
        self.assertEqual(EXPECTED, {name: len(found) for name, found in counted.items()})
        distinct = {pin for found in counted.values() for pin in found}
        self.assertEqual(1, len(distinct), distinct)
        self.assertNotIn(None, distinct)

    def test_a_branch_a_short_commit_and_a_missing_release_are_not_pins(self):
        commit = "a" * 40
        refused = {
            "branch": "uses: jellysin/release-helper/actions/check@main # v1.2.1",
            "short commit": f"uses: jellysin/release-helper/actions/check@{commit[:12]} # v1.2.1",
            "no release named": f"uses: jellysin/release-helper/actions/check@{commit}",
            "a snapshot": f"uses: jellysin/release-helper/.github/workflows/release-please.yml@{commit} # development snapshot",
            "checkout of a tag": "repository: jellysin/release-helper\n          ref: v1.2.1",
        }
        for label, line in refused.items():
            with self.subTest(label):
                self.assertEqual([None], pins(line))
        self.assertEqual([(commit, "v1.2.1")], pins(f"uses: jellysin/release-helper/actions/package@{commit} # v1.2.1"))
        self.assertEqual([(commit, "v1.2.1")], pins(f"repository: jellysin/release-helper\n          ref: {commit} # v1.2.1"))


if __name__ == "__main__":
    unittest.main()
