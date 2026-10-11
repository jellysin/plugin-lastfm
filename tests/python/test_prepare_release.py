"""Release preparation is a manual step on main that writes a tag and a draft through the release helper and nothing else."""

import re
import unittest
from pathlib import Path

from tools.canary import block

WORKFLOW = Path(__file__).resolve().parents[2] / ".github" / "workflows" / "prepare-release.yml"
TRIGGER = ["  workflow_dispatch:", "    inputs:", "      tag:", "      commit:"]
INPUTS = ["          tag: ${{ inputs.tag }}", "          commit: ${{ inputs.commit }}"]
FORBIDDEN = ("actions/publish", "actions/attest", "actions/catalog", "secrets.", "pull_request", "set -x")


def violations(text):
    """Return the limits the preparation workflow text breaks."""
    lines = text.splitlines()
    # The description, required and type lines of an input sit deeper than the names compared here.
    trigger = [line for line in block(lines, "on:") if len(line) - len(line.lstrip()) <= 6]
    wrong = [] if trigger == TRIGGER else ["the only trigger is a manual dispatch with a tag and a commit"]
    if block(lines, "permissions:") != ["  contents: read"] or re.findall(r"^\s*([\w-]+):\s*write\b", text, re.MULTILINE) != ["contents"]:
        wrong.append("the token writes contents in the one job and nothing else")
    # A script would put the typed tag and commit into a shell; the helper's action takes them as values.
    if re.search(r"^\s*(- )?run:", text, re.MULTILINE) or [line for line in lines if "${{ inputs." in line] != INPUTS:
        wrong.append("the typed values reach the helper's action only, never a script")
    if len(re.findall(r"uses: jellysin/release-helper/actions/prepare@", text)) != 1 or len(re.findall(r"^\s*(?:- )?uses:", text, re.MULTILINE)) != 2:
        wrong.append("the checkout and the helper's prepare action are the only steps")
    if "fetch-depth: 0\n" not in text or "persist-credentials: false\n" not in text:
        wrong.append("the checkout has full history and keeps no credentials")
    if not re.search(r"^    timeout-minutes: \d+$", text, re.MULTILINE):
        wrong.append("the job has a time limit")
    return wrong + [f"the workflow does not name '{word}'" for word in FORBIDDEN if word in text]


class PrepareReleaseWorkflowTests(unittest.TestCase):
    def setUp(self):
        self.text = WORKFLOW.read_text(encoding="utf-8")

    def changed(self, old, new):
        self.assertIn(old, self.text)
        return violations(self.text.replace(old, new))

    def test_repository_workflow_keeps_every_limit(self):
        self.assertEqual([], violations(self.text))

    def test_another_trigger_or_input_is_reported(self):
        expected = ["the only trigger is a manual dispatch with a tag and a commit"]
        self.assertEqual(expected, self.changed("on:\n  workflow_dispatch:\n", "on:\n  push:\n    tags: ['v*']\n  workflow_dispatch:\n"))
        self.assertEqual(expected, self.changed("on:\n  workflow_dispatch:\n", "on:\n  schedule:\n    - cron: '0 3 * * *'\n  workflow_dispatch:\n"))
        self.assertEqual(expected, self.changed("      commit:\n", "      publish:\n        type: boolean\n      commit:\n"))

    def test_another_write_permission_is_reported(self):
        expected = ["the token writes contents in the one job and nothing else"]
        self.assertEqual(expected, self.changed("permissions:\n  contents: read\n", "permissions:\n  contents: write\n"))
        self.assertEqual(expected, self.changed("      contents: write\n", "      contents: write\n      id-token: write\n"))
        self.assertEqual(expected, self.changed("    permissions:\n      contents: write\n", ""))

    def test_a_script_or_a_typed_value_elsewhere_is_reported(self):
        expected = ["the typed values reach the helper's action only, never a script"]
        script = "      - run: git tag \"${{ inputs.tag }}\"\n"
        self.assertEqual(expected, self.changed("      - name: Create", script + "      - name: Create"))
        self.assertEqual(expected, self.changed("          token: ${{ github.token }}\n", "          token: ${{ github.token }}\n          version-path: ${{ inputs.tag }}\n"))

    def test_another_step_or_a_missing_helper_is_reported(self):
        expected = ["the checkout and the helper's prepare action are the only steps"]
        self.assertEqual(expected, self.changed("release-helper/actions/prepare@", "release-helper/actions/package@"))
        other = "      - uses: actions/upload-artifact@0000000000000000000000000000000000000000 # v0.0.0\n"
        self.assertEqual(expected, self.changed("      - name: Create", other + "      - name: Create"))

    def test_a_shallow_checkout_kept_credentials_and_no_time_limit_are_reported(self):
        checkout = ["the checkout has full history and keeps no credentials"]
        self.assertEqual(checkout, self.changed("          fetch-depth: 0\n", ""))
        self.assertEqual(checkout, self.changed("persist-credentials: false", "persist-credentials: true"))
        self.assertEqual(["the job has a time limit"], self.changed("    timeout-minutes: 10\n", ""))

    def test_publication_is_reported(self):
        self.assertEqual(["the workflow does not name 'actions/publish'"],
                         self.changed("changelog-path: CHANGELOG.md\n", "changelog-path: CHANGELOG.md # then actions/publish\n"))


if __name__ == "__main__":
    unittest.main()
