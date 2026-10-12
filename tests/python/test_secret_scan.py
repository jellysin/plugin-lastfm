"""Secret-scan exceptions stay fingerprint-scoped and stop applying when they expire."""

import contextlib
import datetime
import io
import unittest
from pathlib import Path
from unittest.mock import patch

from tools import secret_scan
from tools.secret_scan import ScanError, parse_exceptions, scan

COMMIT = "0123456789abcdef0123456789abcdef01234567"
FINGERPRINT = f"{COMMIT}:src/Example.cs:generic-api-key:12"
TODAY = datetime.date(2026, 10, 11)


def listing(expires, fingerprint=FINGERPRINT):
    return f"# an explanatory comment\n# expires {expires}: public upstream value\n{fingerprint}\n"


class ExceptionListTests(unittest.TestCase):
    def test_reads_fingerprint_with_expiry(self):
        (entry,) = parse_exceptions(listing("2027-01-06"))
        self.assertEqual(FINGERPRINT, entry.fingerprint)
        self.assertTrue(entry.active(TODAY))

    def test_expiry_date_itself_is_no_longer_active(self):
        (entry,) = parse_exceptions(listing(TODAY.isoformat()))
        self.assertFalse(entry.active(TODAY))
        self.assertTrue(entry.active(TODAY - datetime.timedelta(days=1)))

    def test_rejects_entries_that_could_widen_into_a_tier(self):
        rejected = {
            "no expiry": f"{FINGERPRINT}\n",
            "no reason": f"# expires 2027-01-06:\n{FINGERPRINT}\n",
            "impossible date": listing("2027-02-30"),
            "path-only fingerprint": listing("2027-01-06", "src/Example.cs:generic-api-key:12"),
            "short commit": listing("2027-01-06", f"{COMMIT[:7]}:src/Example.cs:generic-api-key:12"),
            "wildcard path": listing("2027-01-06", f"{COMMIT}:src/*:generic-api-key:12"),
            "one expiry for two entries": listing("2027-01-06") + f"{COMMIT}:src/Other.cs:generic-api-key:3\n",
            "duplicate": listing("2027-01-06") + listing("2027-01-06"),
            "dangling expiry": listing("2027-01-06") + "# expires 2027-01-06: nothing follows\n",
        }
        for label, text in rejected.items():
            with self.subTest(label), self.assertRaises(ScanError):
                parse_exceptions(text)

    def test_committed_list_is_valid_and_bounded(self):
        entries = parse_exceptions(Path(secret_scan.EXCEPTIONS).read_text(encoding="utf-8"))
        self.assertEqual(4, len(entries))
        self.assertTrue(all(entry.expires <= datetime.date(2027, 1, 6) for entry in entries))


class ScanDecisionTests(unittest.TestCase):
    def decide(self, expires, found):
        entries = parse_exceptions(listing(expires))
        with patch.object(secret_scan, "load_exceptions", return_value=entries), patch.object(secret_scan, "findings", return_value=found):
            return scan("gitleaks", ".", TODAY)

    def test_unexpired_exception_allows_only_its_own_finding(self):
        other = f"{COMMIT}:src/Other.cs:generic-api-key:3"
        self.assertEqual(([FINGERPRINT], [other], []), self.decide("2027-01-06", [FINGERPRINT, other]))

    def test_expired_exception_blocks_like_a_missing_one(self):
        self.assertEqual(([], [FINGERPRINT], []), self.decide("2026-10-11", [FINGERPRINT]))

    def test_reports_an_exception_that_matches_nothing(self):
        self.assertEqual(([], [], [FINGERPRINT]), self.decide("2027-01-06", []))


class SelfTestTests(unittest.TestCase):
    def run_self_test(self, found):
        stderr = io.StringIO()
        with (
            patch.object(secret_scan, "fixture", side_effect=lambda root, name, side_branch=False: Path(root) / name),
            patch.object(secret_scan, "findings", return_value=found),
            contextlib.redirect_stderr(stderr),
            contextlib.redirect_stdout(io.StringIO()),
        ):
            status = secret_scan.main(["--gitleaks", "gitleaks", "--self-test"])
        return status, stderr.getvalue()

    def test_scanner_that_finds_no_planted_key_fails_with_a_clear_message(self):
        status, stderr = self.run_self_test([])
        self.assertEqual(2, status)
        self.assertIn("self-test fixture 'valid': expected 1 planted finding, got 0", stderr)

    def test_scanner_that_reports_extra_findings_fails_with_a_clear_message(self):
        status, stderr = self.run_self_test([FINGERPRINT, f"{COMMIT}:src/Other.cs:generic-api-key:3"])
        self.assertEqual(2, status)
        self.assertIn("expected 1 planted finding, got 2", stderr)


if __name__ == "__main__":
    unittest.main()
