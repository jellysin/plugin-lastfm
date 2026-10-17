"""The delivery check of tools/library_load.py counts every listen of every account and refuses a lost, repeated or misdirected one."""

import sys
import unittest
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tools"))
from scrobble_delivery import (ACCOUNTS, ARTIST, SILENT, SILENT_PLAYED_SECONDS, SILENT_TRACK_SECONDS, TITLES, answered,  # noqa: E402
                               listens, received, verdict)
from smoke import SmokeFailure  # noqa: E402

OPEN = f"{ARTIST} - {TITLES[0]}"


def accepted(label, *tracks):
    return f"POST track.scrobble as {label}" + "".join(" | " + track for track in tracks)


class DeliveryVerdictTests(unittest.TestCase):
    def test_every_listen_of_every_account_once_passes(self):
        lines = ["POST auth.getSession", *(accepted(label, OPEN) for label in ACCOUNTS), f"POST track.updateNowPlaying as one | {OPEN}"]
        verdict(listens(TITLES[0]), received(lines))
        self.assertEqual(set(ACCOUNTS), answered(lines))

    def test_a_batch_counts_each_of_its_tracks(self):
        refused = f"{ARTIST} - {TITLES[1]}"
        lines = [accepted("one", OPEN, refused), *(accepted(label, OPEN) for label in ACCOUNTS[1:]), *(accepted(label, refused) for label in ACCOUNTS[1:])]
        verdict(listens(TITLES[0]) + listens(TITLES[1]), received(lines))

    def test_a_dropped_listen_fails(self):
        lines = ["POST track.scrobble as one", *(accepted(label, OPEN) for label in ACCOUNTS[1:])]
        self.assertEqual(set(ACCOUNTS), answered(lines))
        with self.assertRaisesRegex(SmokeFailure, r"missing \[\('one'"):
            verdict(listens(TITLES[0]), received(lines))

    def test_a_listen_received_twice_fails(self):
        lines = [*(accepted(label, OPEN) for label in ACCOUNTS), accepted("two", OPEN)]
        with self.assertRaisesRegex(SmokeFailure, r"another account \[\('two'"):
            verdict(listens(TITLES[0]), received(lines))

    def test_a_listen_for_another_or_an_unknown_account_fails(self):
        for stray in ("?", "five", ""):
            lines = [*(accepted(label, OPEN) for label in ACCOUNTS[1:]), accepted(stray, OPEN) if stray else f"POST track.scrobble | {OPEN}"]
            with self.subTest(stray=stray), self.assertRaises(SmokeFailure):
                verdict(listens(TITLES[0]), received(lines))

    def test_the_silent_period_expects_one_listen_of_one_account(self):
        silent = f"{ARTIST} - {TITLES[2]}"
        expected = listens(TITLES[0]) + listens(TITLES[2], (SILENT,))
        lines = [*(accepted(label, OPEN) for label in ACCOUNTS), accepted(SILENT, silent)]
        verdict(expected, received(lines))
        for stray in (lines[:-1], [*lines, accepted(SILENT, silent)], [*lines[:-1], accepted(ACCOUNTS[1], silent)]):
            with self.subTest(lines=stray[len(ACCOUNTS):]), self.assertRaises(SmokeFailure):
                verdict(expected, received(stray))

    def test_the_silent_play_needs_the_servers_own_ticks(self):
        # One report credits at most 45 seconds, so the stop alone cannot carry the play past half of its track.
        self.assertGreater(SILENT_PLAYED_SECONDS, 45 + 2)
        self.assertGreaterEqual(SILENT_PLAYED_SECONDS, SILENT_TRACK_SECONDS / 2 + 4)
        self.assertLess(SILENT_PLAYED_SECONDS, SILENT_TRACK_SECONDS)

    def test_refused_attempts_are_counted_apart_from_accepted_ones(self):
        lines = [f"REFUSED {accepted(label, OPEN)}" for label in ACCOUNTS]
        self.assertEqual(Counter(), received(lines))
        self.assertEqual(listens(TITLES[0]), received(lines, refused=True))
        self.assertEqual(set(ACCOUNTS), answered(lines, refused=True))
        self.assertEqual(set(), answered(lines))
        with self.assertRaises(SmokeFailure):
            verdict(listens(TITLES[0]), received(lines))


if __name__ == "__main__":
    unittest.main()
