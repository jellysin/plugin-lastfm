"""The native library checks compare against a recorded table and key the playlist rule on the server release."""

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tests" / "integration"))
import native_library  # noqa: E402
from native_library import ABSENT, ENTITIES, FIXTURES, KINDS, PUBLIC, TRACKS, differences, hides_blocked_playlists, match_checks  # noqa: E402

TRACK = "0a" * 16
OTHER = "0b" * 16
PROBE_LIMIT = 40


class Library:
    """Answer the listings and the probe the way a scanned fixture library does, minus the named items."""

    def __init__(self, without=(), unmatched=()):
        self.without, self.unmatched = set(without), set(unmatched)
        self.ids = {}

    def identifier(self, name):
        return self.ids.setdefault(name, f"{len(self.ids) + 1:032x}")

    def request(self, path, method="GET", body=None, **_):
        if method == "POST":
            return {"tracks": [self.track(query) for query in body["Tracks"]], "entities": [self.entity(query) for query in body["Entities"]]}
        if path.endswith("Audio"):
            stems = [name for name, *_ in FIXTURES] + [PUBLIC]
            return {"items": [{"path": f"/media/Match/{stem}.wav", "id": self.identifier(stem)} for stem in stems if stem not in self.without]}
        artists = [("MusicArtist", artist) for _, _, artist, _ in FIXTURES]
        albums = [("MusicAlbum", album) for *_, album in FIXTURES]
        return {"items": [{"type": kind, "name": name, "id": self.identifier(name)} for kind, name in artists + albums if name not in self.without]}

    def track(self, query):
        file = dict(TRACKS)[(query["Artist"], query["Title"], query["Album"])]
        if file is None or file in self.unmatched:
            return {"status": "missing"}
        return {"status": "matched", "itemid": self.identifier(file)}

    def entity(self, query):
        name = dict(ENTITIES)[(query["Kind"], query["Name"], query["Artist"])]
        return {"itemid": self.identifier(name)} if name and name not in self.unmatched else {}


class ReleaseTests(unittest.TestCase):
    def test_playlist_rule_starts_with_jellyfin_12_1(self):
        recorded = {"12.0.0": False, "12.0.9": False, "12.1.0": True, "12.2.0": True, "12.10.0": True, "13.0.0": True}
        self.assertEqual(recorded, {version: hides_blocked_playlists(version) for version in recorded})

    def test_refuses_a_version_it_cannot_read(self):
        for version in ("", "12", "nightly", "12.x.0"):
            with self.subTest(version=version), self.assertRaises(ValueError):
                hides_blocked_playlists(version)


class DifferenceTests(unittest.TestCase):
    def test_recorded_answers_pass(self):
        expected = [("found", TRACK), ("found with dashes", TRACK), ("absent", None), ("entity", TRACK), ("no entity", None)]
        dashed = f"{TRACK[:8]}-{TRACK[8:12]}-{TRACK[12:16]}-{TRACK[16:20]}-{TRACK[20:]}".upper()
        outcomes = [{"status": "matched", "itemid": TRACK}, {"status": "matched", "itemid": dashed}, {"status": "missing"}, {"itemid": TRACK}, {}]
        self.assertEqual([], differences(expected, outcomes))

    def test_every_kind_of_changed_answer_is_reported(self):
        changed = {
            "another item": ((TRACK, {"status": "matched", "itemid": OTHER}), "matched"),
            "no longer matched": ((TRACK, {"status": "missing"}), "missing"),
            "newly matched": ((None, {"status": "matched", "itemid": TRACK}), "matched"),
            "ambiguous instead of missing": ((None, {"status": "ambiguous"}), "ambiguous"),
            "matched without an item": ((TRACK, {"status": "matched"}), "matched"),
            "entity lost": ((TRACK, {}), "missing"),
            "entity gained": ((None, {"itemid": TRACK}), "matched"),
            "expected item not in the library": ((ABSENT, {"status": "missing"}), "missing"),
        }
        for label, ((item, outcome), status) in changed.items():
            with self.subTest(label):
                self.assertEqual([f"{label} ({status})"], differences([(label, item)], [outcome]))

    def test_refuses_a_different_number_of_answers(self):
        with self.assertRaises(ValueError):
            differences([("one", None), ("two", None)], [{}])


class TableTests(unittest.TestCase):
    def test_every_expectation_names_a_fixture_and_fits_the_probe(self):
        files = {name for name, *_ in FIXTURES} | {PUBLIC}
        names = {artist for _, _, artist, _ in FIXTURES} | {album for *_, album in FIXTURES}
        self.assertTrue({file for _, file in TRACKS if file} <= files)
        self.assertTrue({name for _, name in ENTITIES if name} <= names)
        self.assertTrue({kind for (kind, _, _), _ in ENTITIES} <= set(KINDS))
        for table in (TRACKS, ENTITIES):
            self.assertTrue(0 < len(table) <= PROBE_LIMIT)
            self.assertTrue(any(expected for _, expected in table) and any(not expected for _, expected in table))


class MatchCheckTests(unittest.TestCase):
    def test_recorded_library_passes(self):
        self.assertEqual({"track_queries": len(TRACKS), "entity_queries": len(ENTITIES)}, match_checks(Library(), {"id": TRACK}))

    def test_missing_library_item_and_lost_match_are_named(self):
        planted = {
            "artist / AC/DC (matched)": Library(without={"AC/DC"}),
            "AC/DC / Thunderstruck (missing)": Library(unmatched={"thunder"}),
            "album / Tribute / Thunder Tribute (missing)": Library(unmatched={"Tribute"}),
            "Thunder Tribute / Thunderstruck (missing)": Library(without={"tribute"}, unmatched={"tribute"}),
            "artist / Björk (missing)": Library(without={"Björk"}, unmatched={"Björk"}),
        }
        for named, library in planted.items():
            with self.subTest(named), self.assertRaises(native_library.SmokeFailure) as failure:
                match_checks(library, {"id": TRACK})
            self.assertIn(named, str(failure.exception))


if __name__ == "__main__":
    unittest.main()
