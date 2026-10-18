"""Every workflow reference to the release helper names one commit and says which release it is, and the update bot reads each one."""

import json
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = ROOT / ".github" / "workflows"
UPDATES = json.loads((ROOT / "renovate.json").read_text(encoding="utf-8"))
HELPER = "jellysin/release-helper"
PIN = r"([0-9a-f]{40}) # (v\d+\.\d+\.\d+)$"
USED = re.compile(r"uses: jellysin/release-helper/[A-Za-z0-9_./-]+@(\S+)(.*)$", re.MULTILINE)
CHECKED_OUT = re.compile(r"repository: jellysin/release-helper\n\s+ref: (\S+)(.*)$", re.MULTILINE)
MENTION = re.compile(r"jellysin/release-helper")
EXPECTED = {"build-signed.yml": 2, "ci.yml": 3, "prepare-release.yml": 1, "release.yml": 1, "release-automation.yml": 1}


def pins(text):
    """Return (commit, release) for each helper reference, or None for one that is not a full commit with its release."""
    found = []
    for pattern in (USED, CHECKED_OUT):
        for reference, comment in pattern.findall(text):
            match = re.fullmatch(PIN, reference + comment)
            found.append(match.groups() if match else None)
    return found


def bot_drift(files, updates):
    """Return why the update bot would move the helper actions without the checkouts, each as one statement.

    The github-actions manager reads only the uses: lines; one regex entry reads the checkout refs.
    """
    managers = [entry for entry in updates["customManagers"] if entry.get("depNameTemplate") == HELPER]
    if len(managers) != 1 or managers[0].get("datasourceTemplate") != "github-tags":
        return ["no single update entry reads the helper checkouts"]
    scope = re.compile(managers[0]["managerFilePatterns"][0].strip("/"))
    pattern = re.compile(managers[0]["matchStrings"][0].replace("(?<", "(?P<"))
    problems = []
    for name, text in files.items():
        read = [(match["currentDigest"], match["currentValue"]) for match in pattern.finditer(text)] \
            if scope.search(f".github/workflows/{name}") else []
        if read != [pin for pin in pins(text)[len(USED.findall(text)):] if pin] or len(read) != len(CHECKED_OUT.findall(text)):
            problems.append(f"the update entry reads {read} in {name}")
    grouped = [rule for rule in updates["packageRules"] if rule.get("matchPackageNames") == [HELPER]]
    actions = [rule for rule in updates["packageRules"] if rule.get("matchManagers") == ["github-actions"]]
    if len(grouped) != 1 or len(actions) != 1 or grouped[0].get("groupName") != actions[0].get("groupName"):
        problems.append("no rule puts the helper checkouts into the pull request that moves the actions")
    return problems


def workflows():
    return {path.name: path.read_text(encoding="utf-8") for path in sorted(WORKFLOWS.glob("*.yml"))}


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

    def test_update_bot_moves_every_checkout_with_the_actions(self):
        files = workflows()
        self.assertEqual(2, sum(len(CHECKED_OUT.findall(text)) for text in files.values()))
        self.assertEqual([], bot_drift(files, UPDATES))

    def test_an_unpinned_checkout_a_missing_entry_and_a_missing_group_are_bot_drift(self):
        files = workflows()
        commit, release = pins(files["ci.yml"])[-1]
        unpinned = {**files, "ci.yml": files["ci.yml"].replace(f"ref: {commit} # {release}", "ref: main", 1)}
        without_entry = {**UPDATES, "customManagers": [entry for entry in UPDATES["customManagers"] if entry.get("depNameTemplate") != HELPER]}
        without_group = {**UPDATES, "packageRules": [rule for rule in UPDATES["packageRules"] if rule.get("matchPackageNames") != [HELPER]]}
        self.assertNotEqual(files, unpinned)
        self.assertNotEqual([], bot_drift(unpinned, UPDATES))
        self.assertNotEqual([], bot_drift(files, without_entry))
        self.assertNotEqual([], bot_drift(files, without_group))


if __name__ == "__main__":
    unittest.main()
