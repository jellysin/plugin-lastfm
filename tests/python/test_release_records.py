"""Every release record's `gh release view` read-back asks for the assets whose digests the record lists."""

import re
import unittest
from pathlib import Path

RECORDS = Path(__file__).resolve().parents[2] / "docs" / "validation"
READ_BACK = re.compile(r"gh release view \S+(?: -R \S+)? --json ([A-Za-z,]+)")


def missing_assets(text):
    """The `--json` field lists of `gh release view` commands in text that lack `assets`."""
    return [fields for fields in READ_BACK.findall(text) if "assets" not in fields.split(",")]


class ReleaseRecordTests(unittest.TestCase):
    """The committed release records and the field-list check that guards them."""

    def test_every_record_reads_back_its_assets(self):
        """Each docs/validation/release-*.md has a read-back, and every read-back asks for assets."""
        records = sorted(RECORDS.glob("release-*.md"))
        self.assertTrue(records, f"no release record under {RECORDS}")
        for record in records:
            text = record.read_text(encoding="utf-8")
            with self.subTest(record=record.name):
                self.assertTrue(READ_BACK.search(text), "record has no gh release view read-back")
                self.assertEqual(missing_assets(text), [])

    def test_field_list_without_assets_is_reported(self):
        """A field list without assets is returned as missing."""
        text = "`gh release view v9.9.9 -R jellysin/plugin-lastfm --json isDraft,isImmutable,publishedAt`"
        self.assertEqual(missing_assets(text), ["isDraft,isImmutable,publishedAt"])

    def test_assets_at_the_end_of_the_field_list_is_accepted(self):
        """assets as the last field still counts."""
        text = "`gh release view v9.9.9 --json isDraft,assets`"
        self.assertEqual(missing_assets(text), [])


if __name__ == "__main__":
    unittest.main()
