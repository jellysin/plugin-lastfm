"""Unit tests for pure parts of the state carry-over check: assembly extraction and CLI parsing."""

from __future__ import annotations

import contextlib
import hashlib
import io
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
sys.path.insert(0, str(ROOT / "tests" / "integration"))
from smoke import SmokeFailure  # noqa: E402
import state_carryover  # noqa: E402
from state_carryover import (  # noqa: E402
    PLUGIN_DLL,
    HttpsOnlyRedirects,
    build_parser,
    download_release,
    extract_assembly,
)


def make_zip(entries: dict[str, bytes], compression: int = zipfile.ZIP_STORED) -> bytes:
    """An in-memory zip archive holding the given members."""
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", compression=compression) as bundle:
        for name, data in entries.items():
            bundle.writestr(name, data)
    return buffer.getvalue()


class ExtractAssemblyTests(unittest.TestCase):
    def test_valid_assembly_extracted(self):
        content = b"assembly-payload-bytes"
        archive = make_zip({PLUGIN_DLL: content, "LICENSE": b"MIT"})
        expected_sha = hashlib.sha256(archive).hexdigest()
        with tempfile.TemporaryDirectory() as temp_dir:
            dest = Path(temp_dir)
            extracted = extract_assembly(archive, dest, expected_sha256=expected_sha)
            self.assertEqual(extracted, dest / PLUGIN_DLL)
            self.assertEqual(extracted.read_bytes(), content)

    def test_sha_mismatch_refused(self):
        archive = make_zip({PLUGIN_DLL: b"content"})
        with tempfile.TemporaryDirectory() as temp_dir:
            with self.assertRaisesRegex(SmokeFailure, "SHA-256"):
                extract_assembly(archive, Path(temp_dir), expected_sha256="0" * 64)

    def test_missing_member_refused(self):
        archive = make_zip({"OtherPlugin.dll": b"content"})
        expected_sha = hashlib.sha256(archive).hexdigest()
        with tempfile.TemporaryDirectory() as temp_dir:
            with self.assertRaisesRegex(SmokeFailure, f"no single {PLUGIN_DLL}"):
                extract_assembly(archive, Path(temp_dir), expected_sha256=expected_sha)

    def test_multiple_members_refused(self):
        archive = make_zip({PLUGIN_DLL: b"content1", f"sub/{PLUGIN_DLL}": b"content2"})
        expected_sha = hashlib.sha256(archive).hexdigest()
        with tempfile.TemporaryDirectory() as temp_dir:
            with self.assertRaisesRegex(SmokeFailure, f"no single {PLUGIN_DLL}"):
                extract_assembly(archive, Path(temp_dir), expected_sha256=expected_sha)

    def test_oversize_archive_refused(self):
        archive = make_zip({PLUGIN_DLL: b"content"})
        expected_sha = hashlib.sha256(archive).hexdigest()
        with tempfile.TemporaryDirectory() as temp_dir:
            with self.assertRaisesRegex(SmokeFailure, "exceeding the bound"):
                extract_assembly(archive, Path(temp_dir), expected_sha256=expected_sha, max_bytes=10)

    def test_oversize_member_refused(self):
        content = b"x" * 2000
        archive = make_zip({PLUGIN_DLL: content}, compression=zipfile.ZIP_DEFLATED)
        expected_sha = hashlib.sha256(archive).hexdigest()
        with tempfile.TemporaryDirectory() as temp_dir:
            with self.assertRaisesRegex(SmokeFailure, "exceeds"):
                extract_assembly(archive, Path(temp_dir), expected_sha256=expected_sha, max_bytes=500)


class ArgumentParsingTests(unittest.TestCase):
    def test_plant_drop_keys_accepted(self):
        parser = build_parser()
        args = parser.parse_args(["--plugin", "plugin.dll", "--plant", "drop-keys"])
        self.assertEqual(args.plant, "drop-keys")

    def test_plant_invalid_choice_refused(self):
        parser = build_parser()
        with contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit):
                parser.parse_args(["--plugin", "plugin.dll", "--plant", "unknown-plant"])

    def test_previous_release_flag(self):
        parser = build_parser()
        args = parser.parse_args(["--plugin", "plugin.dll", "--previous-release"])
        self.assertTrue(args.previous_release)

    def test_report_argument(self):
        parser = build_parser()
        args = parser.parse_args(["--plugin", "plugin.dll", "--report", "report.json"])
        self.assertEqual(args.report, Path("report.json"))


class DownloadReleaseTests(unittest.TestCase):
    def test_refuses_non_https_url(self):
        with self.assertRaisesRegex(SmokeFailure, "HTTPS"):
            download_release(url="http://insecure.invalid/plugin.zip")

    def test_refuses_redirect_off_https(self):
        handler = HttpsOnlyRedirects()
        request = urllib.request.Request("https://github.com/plugin.zip")
        for target in ("http://objects.invalid/plugin.zip", "file:///etc/passwd", "ftp://objects.invalid/plugin.zip"):
            with self.subTest(target=target), self.assertRaisesRegex(SmokeFailure, "off HTTPS"):
                handler.redirect_request(request, None, 302, "Found", {}, target)

    def test_follows_redirect_to_https(self):
        handler = HttpsOnlyRedirects()
        request = urllib.request.Request("https://github.com/plugin.zip")
        followed = handler.redirect_request(request, None, 302, "Found", {}, "https://objects.invalid/plugin.zip")
        self.assertEqual(followed.full_url, "https://objects.invalid/plugin.zip")


class ExitStatusTests(unittest.TestCase):
    def test_failure_after_pass_flag_exits_one(self):
        def passes_then_fails(*_args, report, **_kwargs):
            report["passed"] = True
            raise SmokeFailure("Some carry-over resources could not be removed: ['volume'].")

        with tempfile.TemporaryDirectory() as directory, \
                mock.patch.object(state_carryover, "run", side_effect=passes_then_fails), \
                contextlib.redirect_stderr(io.StringIO()):
            plugin = Path(directory) / "plugin.dll"
            plugin.write_bytes(b"dll")
            self.assertEqual(state_carryover.main(["--plugin", str(plugin)]), 1)

    def test_pass_exits_zero(self):
        def passes(*_args, report, **_kwargs):
            report["passed"] = True

        with tempfile.TemporaryDirectory() as directory, \
                mock.patch.object(state_carryover, "run", side_effect=passes), \
                contextlib.redirect_stdout(io.StringIO()):
            plugin = Path(directory) / "plugin.dll"
            plugin.write_bytes(b"dll")
            self.assertEqual(state_carryover.main(["--plugin", str(plugin)]), 0)


if __name__ == "__main__":
    unittest.main()
