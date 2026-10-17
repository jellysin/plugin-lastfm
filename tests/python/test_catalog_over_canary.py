"""The catalog check's package matches the release shape, and its judgement refuses a canary that still loads."""

import hashlib
import json
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tests" / "integration"))
from catalog_over_canary import CANARY_FOLDER, DESCRIPTOR, RELEASE_VERSION, judge_listing, write_repository  # noqa: E402
from smoke import SmokeFailure  # noqa: E402

RELEASE = {"name": DESCRIPTOR["name"], "version": RELEASE_VERSION, "status": "Active"}


def folder(status):
    return {"files": ["JellySin.Plugin.Lastfm.dll", "meta.json"], "meta": {"version": "0.0.5.0", "status": status}}


class RepositoryTests(unittest.TestCase):
    def test_the_manifest_names_the_archive_by_its_checksum(self):
        with tempfile.TemporaryDirectory() as name:
            directory = Path(name)
            (directory / "plugin.dll").write_bytes(b"assembly")
            write_repository(directory / "plugin.dll", directory)
            plugin = json.loads((directory / "manifest.json").read_text(encoding="utf-8"))[0]
            version = plugin["versions"][0]
            archive = directory / version["sourceUrl"].rsplit("/", 1)[1]
            self.assertEqual(DESCRIPTOR["guid"], plugin["guid"])
            self.assertEqual(hashlib.md5(archive.read_bytes(), usedforsecurity=False).hexdigest(), version["checksum"])
            with zipfile.ZipFile(archive) as bundle:
                self.assertEqual([DESCRIPTOR["assemblyFile"], "LICENSE"], bundle.namelist())
                self.assertEqual({zipfile.ZIP_STORED}, {entry.compress_type for entry in bundle.infolist()})
                self.assertEqual(b"assembly", bundle.read(DESCRIPTOR["assemblyFile"]))


class JudgeListingTests(unittest.TestCase):
    def test_the_release_alone_passes_with_the_canary_folder_removed_or_not_loading(self):
        for folders in ({}, {CANARY_FOLDER: folder("Superseded")}, {CANARY_FOLDER: folder("Deleted")}):
            with self.subTest(folders):
                judge_listing({"plugins": [RELEASE], "folders": folders})

    def test_a_canary_folder_that_still_loads_is_refused(self):
        with self.assertRaisesRegex(SmokeFailure, "canary folder still loads"):
            judge_listing({"plugins": [RELEASE], "folders": {CANARY_FOLDER: folder("Active")}})

    def test_a_second_entry_or_another_version_is_refused(self):
        canary = {"name": DESCRIPTOR["name"], "version": "0.0.5.0", "status": "Superseded"}
        for plugins in ([RELEASE, canary], [canary], [{**RELEASE, "status": "Restart"}], []):
            with self.subTest(plugins), self.assertRaisesRegex(SmokeFailure, "not the release alone"):
                judge_listing({"plugins": plugins, "folders": {}})


if __name__ == "__main__":
    unittest.main()
