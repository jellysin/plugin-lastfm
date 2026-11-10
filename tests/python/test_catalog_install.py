"""The catalog install check asks the package API for the named catalog and version, and refuses any other listing."""

import argparse
import json
import sys
import tempfile
import unittest
import urllib.parse
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tests" / "integration"))
from catalog_install import (
    CATALOG,
    catalog_address,
    catalog_version,
    judge,
)
from catalog_over_canary import (
    DESCRIPTOR,
    MANIFEST_URL,
    PACKAGE_SECONDS,
    RELEASE_VERSION,
    finish,
    install,
)
from smoke import PLUGIN_ID, SmokeFailure

VERSION = "0.5.0.0"
ENTRY = {"name": DESCRIPTOR["name"], "version": VERSION, "status": "Active"}


class RecordingAdmin:
    """Answers like a server whose catalog lists the plugin once offered_from package polls have come back empty."""

    def __init__(self, offered_from=0):
        self.calls = []
        self.offered_from = offered_from
        self.polls = 0

    def request(self, path, method="GET", body=None, expected=200, timeout=20, **_):
        """Records the call; a package poll returns the plugin once offered_from earlier polls came back empty."""
        self.calls.append({"method": method, "path": path, "body": body, "expected": expected, "timeout": timeout})
        if path != "/Packages":
            return None
        self.polls += 1
        return [{"guid": PLUGIN_ID}] if self.polls > self.offered_from else []


def installed(call):
    """The path and the decoded query of an install request."""
    parts = urllib.parse.urlsplit(call["path"])
    return parts.path, urllib.parse.parse_qs(parts.query, strict_parsing=True)


class InstallTests(unittest.TestCase):
    def test_the_published_catalog_is_added_and_the_version_installed_from_it(self):
        admin = RecordingAdmin()
        install(admin, CATALOG, VERSION, 120)
        added, install_call = admin.calls[0], admin.calls[-1]
        self.assertEqual(("POST", "/Repositories", 204), (added["method"], added["path"], added["expected"]))
        self.assertEqual([{"Name": "JellySin catalog check", "Url": CATALOG, "Enabled": True}], added["body"])
        self.assertEqual(("POST", 204, 120), (install_call["method"], install_call["expected"], install_call["timeout"]))
        path, query = installed(install_call)
        self.assertEqual("/Packages/Installed/JellySin%20Last.fm", path)
        self.assertEqual({"assemblyGuid": [DESCRIPTOR["guid"]], "version": [VERSION], "repositoryUrl": [CATALOG]}, query)

    def test_the_local_catalog_check_keeps_its_own_catalog_version_and_bound(self):
        admin = RecordingAdmin()
        install(admin)
        self.assertEqual(MANIFEST_URL, admin.calls[0]["body"][0]["Url"])
        _, query = installed(admin.calls[-1])
        self.assertEqual(([RELEASE_VERSION], [MANIFEST_URL]), (query["version"], query["repositoryUrl"]))
        self.assertEqual(PACKAGE_SECONDS, admin.calls[-1]["timeout"])

    def test_the_install_waits_until_the_catalog_lists_the_plugin(self):
        admin = RecordingAdmin(offered_from=2)
        with mock.patch("catalog_over_canary.time.sleep") as sleep:
            install(admin, CATALOG, VERSION, 120)
        self.assertEqual(3, admin.polls)
        self.assertEqual(2, sleep.call_count)
        self.assertTrue(admin.calls[-1]["path"].startswith("/Packages/Installed/"))

    def test_a_catalog_that_never_lists_the_plugin_fails_without_an_install_request(self):
        admin = RecordingAdmin(offered_from=10**6)
        with self.assertRaisesRegex(SmokeFailure, "does not list the package of the repository at " + CATALOG):
            install(admin, CATALOG, VERSION, 0)
        self.assertFalse([call for call in admin.calls if call["path"].startswith("/Packages/Installed/")])


class JudgeTests(unittest.TestCase):
    def test_the_version_alone_and_active_passes(self):
        judge([ENTRY], VERSION, "After restart 1")

    def test_no_entry_another_status_another_version_or_a_second_entry_is_refused(self):
        """An empty listing, a plugin waiting for a restart, an older version and a duplicate entry all fail."""
        older = {**ENTRY, "version": "0.4.0.0"}
        for entries in ([], [{**ENTRY, "status": "Restart"}], [older], [ENTRY, older], [ENTRY, ENTRY]):
            with self.subTest(entries), self.assertRaisesRegex(SmokeFailure, "not 0.5.0.0 alone and active"):
                judge(entries, VERSION, "After restart 2")

    def test_the_planted_pre_install_listing_is_refused_and_named(self):
        with self.assertRaisesRegex(SmokeFailure, r"^Before the install the server lists \[\], not 0.5.0.0 alone"):
            judge([], VERSION, "Before the install")


class ArgumentTests(unittest.TestCase):
    def test_four_part_versions_pass_up_to_nine_digits_a_part(self):
        """Zero in each position, a two-digit part and a nine-digit part are four-part versions."""
        for version in ("0.5.0.0", "12.0.0.0", "0.0.0.1", "123456789.0.0.0"):
            with self.subTest(version):
                self.assertEqual(version, catalog_version(version))

    def test_tags_short_long_padded_and_oversized_versions_are_refused(self):
        """A tag name, three or five parts, a leading zero, a letter, a trailing newline or ten digits are refused."""
        for version in ("0.5.0", "v0.5.0.0", "0.5.0.0.0", "00.5.0.0", "0.5.0.a", "", "0.5.0.0\n", "1234567890.0.0.0"):
            with self.subTest(version), self.assertRaises(argparse.ArgumentTypeError):
                catalog_version(version)

    def test_only_an_https_catalog_with_a_host_passes(self):
        """The published catalog passes; plain HTTP, a missing host, a file address and a bare path are refused."""
        self.assertEqual(CATALOG, catalog_address(CATALOG))
        for address in (CATALOG.replace("https", "http", 1), "https://", "file:///manifest.json", "raw.githubusercontent.com/m"):
            with self.subTest(address), self.assertRaises(argparse.ArgumentTypeError):
                catalog_address(address)


class Part:
    """A disposable server part that records its removal and can fail it."""

    def __init__(self, started=True, fails=False):
        self.name = "jellysin-smoke-test"
        self.started = started
        self.fails = fails
        self.removed = False

    def cleanup(self):
        """Marks the part removed, then fails when told to."""
        self.removed = True
        if self.fails:
            raise SmokeFailure("The part could not be removed.")


class FinishTests(unittest.TestCase):
    def test_the_report_carries_the_plugin_log_and_every_part_is_removed(self):
        """A started server's plugin lines go into the written report, and each part is removed."""
        parts = (Part(), Part())
        with tempfile.TemporaryDirectory() as name, mock.patch("catalog_over_canary.docker", return_value="a\nPlugin b"):
            path = Path(name) / "out" / "report.json"
            finish(parts[0], parts, path, {"passed": True})
            self.assertEqual({"passed": True, "log": ["Plugin b"]}, json.loads(path.read_text(encoding="utf-8")))
        self.assertEqual([True, True], [part.removed for part in parts])

    def test_a_server_that_never_started_gets_no_log_and_is_still_removed(self):
        """Without a started server no log is read, the report is written and the part is removed."""
        host = Part(started=False)
        with tempfile.TemporaryDirectory() as name, mock.patch("catalog_over_canary.docker") as docker:
            path = Path(name) / "report.json"
            finish(host, (host,), path, {"plant": None})
            self.assertEqual({"plant": None}, json.loads(path.read_text(encoding="utf-8")))
        docker.assert_not_called()
        self.assertTrue(host.removed)

    def test_a_failed_log_capture_is_recorded_and_the_server_is_removed(self):
        """A Docker log failure is named in the written report instead of stopping the teardown."""
        host = Part()
        failure = SmokeFailure("Docker logs failed (exit 1).")
        with tempfile.TemporaryDirectory() as name, mock.patch("catalog_over_canary.docker", side_effect=failure):
            path = Path(name) / "report.json"
            finish(host, (host,), path, {})
            self.assertEqual({"log-failure": str(failure)}, json.loads(path.read_text(encoding="utf-8")))
        self.assertTrue(host.removed)

    def test_a_failed_report_write_still_removes_every_part(self):
        """A report path that cannot be created raises, after every part has been removed."""
        parts = (Part(), Part())
        with tempfile.TemporaryDirectory() as name, mock.patch("catalog_over_canary.docker", return_value=""):
            blocker = Path(name) / "file"
            blocker.write_text("", encoding="utf-8")
            with self.assertRaises(OSError):
                finish(parts[0], parts, blocker / "report.json", {})
        self.assertEqual([True, True], [part.removed for part in parts])

    def test_a_failed_removal_still_removes_the_other_parts_and_fails(self):
        """One part that cannot be removed does not keep the next from being removed, and the teardown fails."""
        parts = (Part(fails=True), Part())
        with (
            tempfile.TemporaryDirectory() as name,
            mock.patch("catalog_over_canary.docker", return_value=""),
            self.assertRaisesRegex(SmokeFailure, "^The part could not be removed.$"),
        ):
            finish(parts[0], parts, Path(name) / "report.json", {})
        self.assertEqual([True, True], [part.removed for part in parts])


if __name__ == "__main__":
    unittest.main()
