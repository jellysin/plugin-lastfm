"""Release-please writes the release PR only; the tag and the one draft come from prepare-release.yml alone."""

import json
import unittest
from pathlib import Path

CONFIG = Path(__file__).resolve().parents[2] / "release-please-config.json"
# Each of these makes release-please write a release, or shape one, beside the prepare step.
RELEASE_KEYS = ("draft", "force-tag-creation", "prerelease", "release-label", "skip-github-release")


def unique(pairs):
    """Build an object and refuse a key that appears twice; the last copy would silently win."""
    keys = [key for key, _ in pairs]
    duplicates = sorted({key for key in keys if keys.count(key) > 1})
    if duplicates:
        raise ValueError(f"duplicate keys: {duplicates}")
    return dict(pairs)


def violations(text):
    """Return the limits the release-please configuration text breaks."""
    config = json.loads(text, object_pairs_hook=unique)
    wrong = []
    if config.get("skip-github-release") is not True:
        wrong.append("release-please writes no tag and no release for any package")
    sections = [("the top level", config)] + [(f"package '{path}'", package) for path, package in config.get("packages", {}).items()]
    for name, section in sections:
        extra = sorted(key for key in RELEASE_KEYS if key in section and not (section is config and key == "skip-github-release"))
        wrong += [f"{name} sets no release option '{key}'" for key in extra]
    return wrong


class ReleasePleaseConfigTests(unittest.TestCase):
    def setUp(self):
        self.text = CONFIG.read_text(encoding="utf-8")

    def changed(self, old, new):
        self.assertIn(old, self.text)
        return violations(self.text.replace(old, new, 1))

    def test_repository_configuration_keeps_one_release_writer(self):
        self.assertEqual([], violations(self.text))

    def test_writing_releases_again_is_reported(self):
        expected = ["release-please writes no tag and no release for any package"]
        self.assertEqual(expected, self.changed('"skip-github-release": true', '"skip-github-release": false'))
        self.assertEqual(expected, self.changed('  "skip-github-release": true,\n', ""))

    def test_a_draft_or_forced_tag_is_reported(self):
        self.assertEqual(["the top level sets no release option 'draft'"],
                         self.changed('"skip-github-release": true,', '"skip-github-release": true,\n  "draft": true,'))
        self.assertEqual(["the top level sets no release option 'force-tag-creation'"],
                         self.changed('"skip-github-release": true,', '"skip-github-release": true,\n  "force-tag-creation": true,'))

    def test_a_package_that_writes_its_own_release_is_reported(self):
        self.assertEqual(["package '.' sets no release option 'skip-github-release'"],
                         self.changed('"package-name"', '"skip-github-release": false,\n      "package-name"'))

    def test_a_duplicate_key_is_refused(self):
        with self.assertRaises(ValueError):
            self.changed('"skip-github-release": true,', '"skip-github-release": true,\n  "skip-github-release": false,')


if __name__ == "__main__":
    unittest.main()
