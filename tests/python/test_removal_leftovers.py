"""The removal leftovers check confirms expected state and refuses incomplete removal or lost state."""

from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tests" / "integration"))
from catalog_over_canary import CANARY_FOLDER, DESCRIPTOR, RELEASE_VERSION  # noqa: E402
from removal_leftovers import judge_leftovers, parse_leftovers  # noqa: E402
from smoke import SmokeFailure  # noqa: E402

EXPECTED_LISTING = {
    "configurations/JellySin.Plugin.Lastfm.xml": 1450,
    "jellysin-lastfm/state/cache.json": 320,
    "jellysin-lastfm/keys/key-ring-1.xml": 842,
}


class JudgeLeftoversTests(unittest.TestCase):
    def test_expected_listing_passes(self):
        judge_leftovers(dict(EXPECTED_LISTING))

    def test_assembly_still_in_plugin_folder_is_refused(self):
        folders = (CANARY_FOLDER, f"JellySin Last.fm_{RELEASE_VERSION}", "custom-plugin-dir")
        for folder in folders:
            with self.subTest(folder):
                listing = {**EXPECTED_LISTING, f"{folder}/{DESCRIPTOR['assemblyFile']}": 50000}
                with self.assertRaisesRegex(SmokeFailure, "Plugin assembly still present"):
                    judge_leftovers(listing)

    def test_settings_xml_missing_is_refused(self):
        listing = {k: v for k, v in EXPECTED_LISTING.items() if not k.endswith(".xml")}
        with self.assertRaisesRegex(SmokeFailure, "Configuration file .* missing"):
            judge_leftovers(listing)

    def test_empty_keys_is_refused(self):
        listing = {k: v for k, v in EXPECTED_LISTING.items() if not k.startswith("jellysin-lastfm/keys/")}
        with self.assertRaisesRegex(SmokeFailure, "Expected key files under jellysin-lastfm/keys"):
            judge_leftovers(listing)

    def test_missing_state_is_refused(self):
        listing = {k: v for k, v in EXPECTED_LISTING.items() if not k.startswith("jellysin-lastfm/state/")}
        with self.assertRaisesRegex(SmokeFailure, "Expected state files under jellysin-lastfm/state"):
            judge_leftovers(listing)

    def test_boundary_settings_xml_size_zero_is_refused(self):
        listing = {**EXPECTED_LISTING, "configurations/JellySin.Plugin.Lastfm.xml": 0}
        with self.assertRaisesRegex(SmokeFailure, "empty or has invalid size"):
            judge_leftovers(listing)

    def test_boundary_keys_folder_exists_but_no_files_is_refused(self):
        listing_no_files = {k: v for k, v in EXPECTED_LISTING.items() if not k.startswith("jellysin-lastfm/keys/")}
        for empty_variant in (listing_no_files, {**listing_no_files, "jellysin-lastfm/keys/": 0}):
            with self.subTest(empty_variant):
                with self.assertRaisesRegex(SmokeFailure, "Expected key files under jellysin-lastfm/keys"):
                    judge_leftovers(empty_variant)

    def test_boundary_unrelated_extra_plugin_folder_without_assembly_passes(self):
        listing = {
            **EXPECTED_LISTING,
            "OtherPlugin/OtherPlugin.dll": 12000,
            "OtherPlugin/meta.json": 240,
        }
        judge_leftovers(listing)


class ParseLeftoversTests(unittest.TestCase):
    def test_parse_valid_lines(self):
        output = "configurations/JellySin.Plugin.Lastfm.xml\t1450\njellysin-lastfm/state/cache.json\t320\n"
        self.assertEqual(
            parse_leftovers(output),
            {
                "configurations/JellySin.Plugin.Lastfm.xml": 1450,
                "jellysin-lastfm/state/cache.json": 320,
            },
        )

    def test_parse_invalid_format_raises(self):
        with self.assertRaisesRegex(SmokeFailure, "Invalid find output line"):
            parse_leftovers("invalid_line_without_tab\n")

    def test_parse_too_many_lines_raises(self):
        with self.assertRaisesRegex(SmokeFailure, "more than"):
            parse_leftovers("".join(f"file-{index}\t1\n" for index in range(1001)))

    def test_parse_invalid_size_raises(self):
        with self.assertRaisesRegex(SmokeFailure, "Invalid size for leftover"):
            parse_leftovers("some/file.xml\tnot_a_number\n")


if __name__ == "__main__":
    unittest.main()
