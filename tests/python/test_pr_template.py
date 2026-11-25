"""The pull request template matches the update bot's headings and keeps its prose on single lines."""

import re
import unittest
from itertools import pairwise
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TEMPLATE = ROOT / ".github/pull_request_template.md"
# The update bot's family preset (plurano/horistes default.json prHeader) writes these headings into every bot pull request, and the review bot checks bodies against this template.
PRESET_HEADINGS = ("Description", "Pre-Merge Verification Checklist")
MARKDOWN_HEADING = re.compile(r"^[ \t]{0,3}#{1,6}(?:[ \t]|$)")
LIST_ITEM = re.compile(r"^[ \t]*(?:[-+*]|[0-9]{1,9}[.)])(?:[ \t]|$)")


def heading_problems(text):
    """Return one statement per missing heading, extra heading, or order problem."""
    headings = [line[3:].strip() for line in text.splitlines() if line.startswith("## ")]
    problems = [f"Missing level-2 heading: {heading}" for heading in PRESET_HEADINGS if heading not in headings]
    problems.extend(f"Unexpected level-2 heading: {heading}" for heading in headings if heading not in PRESET_HEADINGS)

    actual_order = [heading for heading in headings if heading in PRESET_HEADINGS]
    expected_order = [heading for heading in PRESET_HEADINGS if heading in headings]
    if actual_order != expected_order:
        problems.append(f"Expected level-2 headings once each in this order: {', '.join(PRESET_HEADINGS)}")
    return problems


def wrapped_lines(text):
    """Return the 1-based starting lines of adjacent prose lines."""
    wrapped = []
    for number, (line, following) in enumerate(pairwise(text.splitlines()), start=1):
        if not line.strip() or not following.strip():
            continue
        if MARKDOWN_HEADING.match(line) or MARKDOWN_HEADING.match(following) or LIST_ITEM.match(following):
            continue
        wrapped.append(number)
    return wrapped


class PullRequestTemplateTests(unittest.TestCase):
    def test_template_matches_preset_and_keeps_prose_unwrapped(self):
        """Check the repository template against both requirements."""
        template = TEMPLATE.read_text(encoding="utf-8")
        self.assertEqual([], heading_problems(template))
        self.assertEqual([], wrapped_lines(template))

    def test_missing_checklist_heading(self):
        """Name the missing preset heading."""
        self.assertEqual(
            ["Missing level-2 heading: Pre-Merge Verification Checklist"],
            heading_problems("## Description\n"),
        )

    def test_old_headings(self):
        """Name both missing preset headings and both extra headings."""
        self.assertCountEqual(
            [
                "Missing level-2 heading: Description",
                "Missing level-2 heading: Pre-Merge Verification Checklist",
                "Unexpected level-2 heading: Change",
                "Unexpected level-2 heading: Validation",
            ],
            heading_problems("## Change\n\n## Validation\n"),
        )

    def test_reordered_headings(self):
        """Report preset headings in the reverse order."""
        self.assertEqual(
            ["Expected level-2 headings once each in this order: Description, Pre-Merge Verification Checklist"],
            heading_problems("## Pre-Merge Verification Checklist\n\n## Description\n"),
        )

    def test_deeper_heading(self):
        """Keep a level-3 Description from satisfying the level-2 requirement."""
        self.assertEqual(
            ["Missing level-2 heading: Description", "Missing level-2 heading: Pre-Merge Verification Checklist"],
            heading_problems("### Description\n"),
        )

    def test_wrapped_paragraph(self):
        """Report the first line of a hard wrap after the heading and blank line."""
        self.assertEqual([3], wrapped_lines("## Description\n\nA wrapped\nparagraph.\n"))


if __name__ == "__main__":
    unittest.main()
