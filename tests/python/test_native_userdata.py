"""The native userdata probe refuses each planted defect in the history, favourite, playlist, scheduler and blocked
refresh answers it reads."""

import json
import sys
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tests" / "integration"))
import native_userdata  # noqa: E402
from native_userdata import (  # noqa: E402
    PROBE, blocked_refresh_checks, favourite_checks, history_checks, playlist_checks, scheduler_checks, validate,
)
from smoke import SmokeFailure, lower_keys  # noqa: E402

USER = "0c" * 16
STRANGER = "0b" * 16
ITEM = {"UserId": USER, "ItemId": "0a" * 16}
PUBLIC = ["0d" * 16, "0e" * 16]
UNFINISHED = "The playlist write did not finish. Recovery can be retried."
PENDING = "/JellySin/Lastfm/Me/Playlists/Pending"
RECIPES = "/JellySin/Lastfm/Me/Playlists"


def dashed(value):
    return f"{value[:8]}-{value[8:12]}-{value[12:16]}-{value[16:20]}-{value[20:]}"


class Server:
    """Answer the probe and the native API the way a correct Jellyfin 12 host does, except for the named faults."""

    def __init__(self, *faults):
        self.faults = set(faults)
        self.origin = "http://127.0.0.1:1"
        self.configuration = {"Enabled": False}
        self.native = {"PlayCount": 0, "LastPlayedDate": None}
        self.favourite, self.revision = False, 0
        self.playlists, self.pending, self.recipes, self.injected = {}, None, [], False
        # The owner's parental limit and the stand-in's count of top-track reads, for the blocked refresh.
        self.policy, self.reads = {"BlockUnratedItems": []}, 0
        # Only the scheduler check's host runs the plugin's scheduler at startup.
        self.scheduler = False

    def answer(self, role, path, method, body):
        route = path.split("?")[0]
        for handler in (self.history, self.favourites, self.settings, self.items, self.users, self.refresh, self.journal,
                        self.playlist):
            answer = handler(role, route, path, method, body)
            if answer is not None:
                return answer
        raise AssertionError(f"Unexpected {method} {route} as {role}.")

    def history(self, role, route, path, method, body):
        if route == PROBE + "/History":
            self.native = {"PlayCount": 5 if "floor hidden" in self.faults else 10, "LastPlayedDate": "2026-01-01T00:00:00Z"}
            return 200, {"StaleCount": 10, "StaleDatePreserved": True, "FreshResetCount": 0, "FreshResetDateCleared": True, "RestoredFloor": 10}
        if not route.startswith("/UserItems/"):
            return None
        if method == "POST" and "reset ignored" not in self.faults:
            self.native = {"PlayCount": body["PlayCount"], "LastPlayedDate": body["LastPlayedDate"]}
        answer = dict(self.native)
        if method == "POST" and "reset reverts" in self.faults:
            self.native["PlayCount"] = 10
        return 200, answer

    def favourites(self, role, route, path, method, body):
        if route == PROBE + "/Favourite/Prepare":
            self.favourite, self.revision = True, self.revision + 1
            return 200, {"Favourite": True, "Revision": self.revision}
        if route == PROBE + "/Favourite/Review":
            removes = body["Revision"] == self.revision or "stale accepted" in self.faults
            self.favourite = self.favourite and not removes
            return 200, {"Accepted": removes or "stale acknowledged" in self.faults, "Favourite": self.favourite, "Revision": self.revision}
        if not route.startswith("/UserFavoriteItems/"):
            return None
        if method == "POST" or "remove ignored" not in self.faults:
            self.favourite = method == "POST"
        self.revision += "revision frozen" not in self.faults
        return 200, {"IsFavorite": self.favourite}

    def settings(self, role, route, path, method, body):
        if not route.endswith("/Configuration"):
            return None
        if method == "POST":
            self.configuration = body
            return 204, None
        return 200, self.configuration

    def items(self, role, route, path, method, body):
        if route != "/Items":
            return None
        if "Audio" in path:
            tracks = [{"Id": item, "Path": f"/media/Public/{index}.wav"} for index, item in enumerate(PUBLIC)]
            return 200, {"Items": tracks + [{"Id": "0f" * 16, "Path": "/media/Hidden/0.wav"}]}
        return 200, {"Items": [{"Id": playlist} for playlist in self.playlists]}

    def users(self, role, route, path, method, body):
        if not route.startswith("/Users/"):
            return None
        if method == "POST":
            self.policy = dict(body)
            return 204, None
        return 200, {"Policy": dict(self.policy)}

    def refresh(self, role, route, path, method, body):
        """Refresh the generated playlist from the stand-in's top tracks, of which only the first is in the library."""
        if route != RECIPES or method != "POST":
            return None
        self.reads += "refused before read" not in self.faults
        (playlist,) = self.playlists
        if self.policy["BlockUnratedItems"]:
            if "blocked leaves pending" in self.faults:
                self.pending = {"OperationId": "operation-2", "Status": UNFINISHED}
            if "blocked clears" in self.faults or "blocked writes" in self.faults:
                self.playlists[playlist] = []
            return (200, {"PlaylistId": playlist}) if "blocked writes" in self.faults else (409, None)
        if "unblocked ignored" not in self.faults:
            self.playlists[playlist] = PUBLIC[:1]
        return 200, {"PlaylistId": playlist}

    def journal(self, role, route, path, method, body):
        if route == PROBE + "/Playlist/Prepare":
            return self.prepare(body)
        if route == PROBE + "/Playlist/Recover":
            return self.recover()
        if route.startswith(PENDING + "/"):
            return (204, None) if "viewer cancels" in self.faults else (404, None)
        if route == PENDING:
            hidden = role == "viewer" and "pending leaks" not in self.faults
            return 200, None if hidden or self.pending is None else dict(self.pending)
        if route == RECIPES:
            return 200, [] if role == "viewer" else list(self.recipes)
        return None

    def prepare(self, body):
        if self.pending is not None:
            if "prepare leaks a playlist" in self.faults:
                self.playlists[f"{len(self.playlists) + 1:032x}"] = []
            return 409, None
        playlist = f"{len(self.playlists) + 1:032x}"
        self.playlists[playlist] = list(body["Items"])
        self.pending = {"OperationId": "operation-1", "Status": "", "PlaylistId": playlist, "Items": list(body["Items"])}
        return 200, {"OperationId": "operation-1", "PlaylistId": playlist, "IntendedItems": list(body["Items"])}

    def recover(self):
        if self.pending is None:
            if "recovery not idempotent" in self.faults:
                for playlist in self.playlists:
                    self.playlists[playlist] = self.playlists[playlist][:1]
            return 200, {"Recovered": False, "InjectedFailure": False}
        playlist = self.pending["PlaylistId"]
        if not self.injected and "no injected failure" not in self.faults:
            self.injected, self.playlists[playlist] = True, []
            self.pending["Status"] = UNFINISHED
            return 200, {"InjectedFailure": True, "Cleared": True, "Recovered": False}
        items = self.pending["Items"]
        self.playlists[playlist] = items[::-1] if "recovery reorders" in self.faults else list(items)
        copies = 2 if "recipe duplicated" in self.faults else 1
        recipe = {"Id": "recipe-1", "Name": "Top", "Source": "Top", "Period": "overall", "Limit": 2, "DailyRefresh": False}
        self.recipes += [{**recipe, "PlaylistId": playlist}] * (copies - len(self.recipes))
        if "journal kept" not in self.faults:
            self.pending = None
        return 200, {"Recovered": True, "InjectedFailure": False}

    def playlist(self, role, route, path, method, body):
        if not route.startswith("/Playlists/"):
            return None
        if role == "viewer" and "viewer reads playlist" not in self.faults:
            return 404, None
        shares = [{"UserId": dashed(USER)}] + ([{"UserId": STRANGER}] if "foreign share" in self.faults else [])
        items = self.playlists[route.removeprefix("/Playlists/")]
        return 200, {"OpenAccess": "public playlist" in self.faults, "Shares": shares, "ItemIds": [dashed(item).upper() for item in items]}

    def restart(self, base_path):
        self.origin = "http://127.0.0.1:2"
        if "status lost on restart" in self.faults and self.pending is not None:
            self.pending["Status"] = ""
        if self.scheduler and "scheduler idle" not in self.faults and self.pending is not None:
            self.recover()


class Account:
    """A signed-in fixture account that checks the status and folds keys like the smoke client."""

    def __init__(self, server, role):
        self.server, self.role, self.origin = server, role, server.origin

    def request(self, path, method="GET", body=None, expected=200, raw=False, **_):
        status, value = self.server.answer(self.role, path, method, body)
        allowed = (expected,) if isinstance(expected, int) else expected
        if status not in allowed:
            raise SmokeFailure(f"{method} {path.split('?')[0]} returned HTTP {status}; expected {allowed}.")
        payload = b"" if value is None else json.dumps(value).encode()
        if raw:
            return payload, {}, status
        return lower_keys(json.loads(payload)) if payload else None


def accounts(server):
    return Account(server, "admin"), Account(server, "listener"), Account(server, "viewer")


def probe(*faults):
    """Run the history, favourite and playlist checks against one host and return the assembled report."""
    server = Server(*faults)
    admin, listener, viewer = accounts(server)
    with mock.patch.object(native_userdata, "other_account", return_value=viewer):
        return {
            "history": history_checks(admin, listener, ITEM),
            "favourites": favourite_checks(admin, listener, ITEM),
            "playlist": playlist_checks(server, admin, listener, {"id": USER}),
            "scheduler_playlist_recovery": scheduled(),
        }


def scheduled(*faults, server=None):
    """Run the scheduler check against a connected listener on a host whose restart starts the plugin's scheduler."""
    server = server or Server(*faults)
    server.scheduler = True
    admin, listener, viewer = accounts(server)
    # A zero wait refuses a write still pending after the restart without sleeping through the real interval.
    with mock.patch.object(native_userdata, "approve"), mock.patch.object(native_userdata, "SCHEDULER_SECONDS", 0):
        return scheduler_checks(server, admin, viewer, listener, {"id": USER})


class StandIn:
    """The stand-in's call log as the blocked refresh reads it: one line per top-track read the host made."""

    def __init__(self, server):
        self.server = server

    def lines(self, name):
        return ["GET user.getTopTracks"] * self.server.reads if name == "calls.log" else []


def refreshed(*faults):
    """Run the blocked refresh against the connected listener whose interrupted write the scheduler recovered."""
    server = Server(*faults)
    scheduled(server=server)
    admin, listener, viewer = accounts(server)
    with mock.patch.object(native_userdata, "other_account", return_value=viewer):
        return blocked_refresh_checks(admin, listener, {"id": USER}, StandIn(server))


class ValidateTests(unittest.TestCase):
    def test_the_answers_of_a_correct_host_pass(self):
        result = probe()
        validate(result)
        self.assertTrue(result["history"]["native_http_reset"])
        self.assertTrue(all(result["playlist"].values()))

    def test_each_planted_report_value_is_refused(self):
        planted = {
            ("history", "stalecount", 3): "stale native snapshot overwrote the imported play-count floor",
            ("history", "staledatepreserved", False): "overwrote the imported last-played date",
            ("history", "freshresetcount", 10): "fresh native reset was blocked",
            ("history", "freshresetdatecleared", False): "fresh native reset was blocked",
            ("history", "restoredfloor", 0): "did not restore the fixture import floor",
            ("favourites", "stale_review_rejected", False): "stale review erased",
            ("favourites", "revision_changed", False): "did not advance the review revision",
            ("favourites", "fresh_review_accepted", False): "current favourite review was incorrectly rejected",
            ("scheduler_playlist_recovery", "scheduler_recovered_without_probe_call", False): "scheduler did not finish the interrupted playlist write",
        }
        for (section, key, value), message in planted.items():
            result = probe()
            result[section][key] = value
            with self.subTest(key=key, value=value), self.assertRaisesRegex(SmokeFailure, message):
                validate(result)


class HostFaultTests(unittest.TestCase):
    def test_each_planted_native_history_or_favourite_fault_is_refused(self):
        planted = {
            "floor hidden": "native HTTP API cannot see the imported floor",
            "reset ignored": "Native HTTP reset did not persist",
            "reset reverts": "Native reset disappeared when read back",
            "remove ignored": "Native favourite removal did not persist",
        }
        for fault, message in planted.items():
            with self.subTest(fault), self.assertRaisesRegex(SmokeFailure, message):
                probe(fault)

    def test_a_review_that_ignores_the_revision_fails_validation(self):
        for fault, message in {
            "stale accepted": "stale review erased",
            "stale acknowledged": "stale review erased",
            "revision frozen": "stale review erased",
        }.items():
            result = probe(fault)
            with self.subTest(fault), self.assertRaisesRegex(SmokeFailure, message):
                validate(result)

    def test_each_planted_playlist_fault_is_refused(self):
        planted = {
            "public playlist": "fixture playlist became public",
            "foreign share": "acquired another user's share",
            "viewer reads playlist": r"returned HTTP 200; expected \(403, 404\)",
            "pending leaks": "Another user saw private pending work",
            "viewer cancels": r"returned HTTP 204; expected \(404,\)",
            "prepare leaks a playlist": "Repeated preparation created another native playlist",
            "no injected failure": "native clear/add fault did not fire",
            "status lost on restart": "journal did not survive restart",
            "recovery reorders": "Recovery changed intended native playlist order",
            "journal kept": "Completed recovery did not clear its journal",
            "recipe duplicated": "Recovery duplicated or lost its playlist recipe",
            "recovery not idempotent": "Completed recovery was not idempotent",
        }
        for fault, message in planted.items():
            with self.subTest(fault), self.assertRaisesRegex(SmokeFailure, message):
                probe(fault)


class SchedulerFaultTests(unittest.TestCase):
    def test_a_host_whose_scheduler_recovers_the_write_passes(self):
        result = scheduled()
        self.assertTrue(all(value is True for key, value in result.items() if key != "scheduler_recovery_seconds_after_restart"))

    def test_each_planted_scheduler_fault_is_refused(self):
        planted = {
            "scheduler idle": "did not finish the interrupted write within 0 seconds of the restart",
            "no injected failure": "native fault did not interrupt the connected user's write",
            "recovery reorders": "scheduler's recovery changed the intended playlist order",
        }
        for fault, message in planted.items():
            with self.subTest(fault), self.assertRaisesRegex(SmokeFailure, message):
                scheduled(fault)


class BlockedRefreshTests(unittest.TestCase):
    def test_a_refresh_that_the_block_refuses_without_a_write_passes(self):
        self.assertTrue(all(refreshed().values()))

    def test_each_planted_blocked_refresh_fault_is_refused(self):
        planted = {
            "refused before read": "refused before it read the owner's Last.fm top tracks",
            "blocked writes": r"returned HTTP 200; expected \(409,\)",
            "blocked leaves pending": "left a pending playlist write",
            "blocked clears": "changed the playlist or its recipe",
            "unblocked ignored": "did not rewrite the playlist",
        }
        for fault, message in planted.items():
            with self.subTest(fault), self.assertRaisesRegex(SmokeFailure, message):
                refreshed(fault)


if __name__ == "__main__":
    unittest.main()
