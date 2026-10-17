#!/usr/bin/env python3
"""Run test-only userdata probes on a new disposable Jellyfin 12 Docker host."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import secrets
import sys
import tempfile
import time
import uuid

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
from smoke import (  # noqa: E402
    Client,
    DockerHost,
    SmokeFailure,
    add_library,
    add_server_options,
    chosen_image,
    docker,
    fixture_music,
    plugin_settings,
    require,
    setup_server,
    wait_music,
)

from native_library import (  # noqa: E402
    FIXTURES,
    LIBRARY,
    limited_owner_checks,
    match_checks,
    match_fixtures,
    other_account,
    set_policy,
    wait_scan,
)
from page_check import StandInContainer  # noqa: E402
from state_carryover import approve  # noqa: E402

PROBE = "/JellySin/NativeProbe"
# The feature scheduler runs at startup and then every five minutes; recovery after a restart may wait one interval.
SCHEDULER_SECONDS = 5 * 60 + 60


def fixture_accounts(admin: Client) -> tuple[Client, dict]:
    setup_server(admin, secrets.token_urlsafe(24))
    return probe_user(admin)


def probe_user(admin: Client) -> tuple[Client, dict]:
    name, password = "JellySin Probe " + uuid.uuid4().hex[:8], secrets.token_urlsafe(24)
    user = admin.request("/Users/New", "POST", {"Name": name, "Password": password})
    require(
        not user["policy"]["isadministrator"],
        "Fixture user unexpectedly has administrator rights.",
    )
    listener = Client(admin.origin)
    listener.authenticate(name, password)
    return listener, user


def fixture_item(admin: Client, user: dict) -> dict:
    plugin_settings(admin, Enabled=False)
    for name in ("Public", "Hidden", LIBRARY):
        add_library(admin, name)
    admin.request("/Library/Refresh", "POST", expected=204)
    items = wait_music(admin, 3 + len(FIXTURES))
    wait_scan(admin)
    item = next(entry for entry in items if "Public" in entry["name"])
    return {"UserId": user["id"], "ItemId": item["id"]}


def history_checks(admin: Client, listener: Client, item: dict) -> dict:
    result = admin.request(PROBE + "/History", "POST", item)
    path = "/UserItems/" + item["ItemId"] + "/UserData"
    native = listener.request(path)
    require(
        native["playcount"] == 10, "The native HTTP API cannot see the imported floor."
    )
    reset = listener.request(
        path,
        "POST",
        {"PlayCount": 0, "Played": False, "LastPlayedDate": "2020-01-01T00:00:00Z"},
    )
    require(
        reset["playcount"] == 0 and reset["lastplayeddate"].startswith("2020-01-01"),
        "Native HTTP reset did not persist.",
    )
    require(
        listener.request(path)["playcount"] == 0,
        "Native reset disappeared when read back.",
    )
    return {**result, "native_http_reset": True}


def favourite_checks(admin: Client, listener: Client, item: dict) -> dict:
    prepared = admin.request(PROBE + "/Favourite/Prepare", "POST", item)
    require(prepared["favourite"], "The fixture favourite was not prepared.")
    path = "/UserFavoriteItems/" + item["ItemId"]
    removed = listener.request(path, "DELETE")
    require(not removed["isfavorite"], "Native favourite removal did not persist.")
    added = listener.request(path, "POST")
    require(added["isfavorite"], "Native favourite re-add did not persist.")
    stale = admin.request(
        PROBE + "/Favourite/Review", "POST", {**item, "Revision": prepared["revision"]}
    )
    fresh = admin.request(
        PROBE + "/Favourite/Review", "POST", {**item, "Revision": stale["revision"]}
    )
    return {
        "native_remove_readd": True,
        "stale_review_rejected": not stale["accepted"] and stale["favourite"],
        "revision_changed": stale["revision"] != prepared["revision"],
        "fresh_review_accepted": fresh["accepted"] and not fresh["favourite"],
    }


def validate(result: dict) -> None:
    history = result["history"]
    require(
        history["stalecount"] == 10,
        "A stale native snapshot overwrote the imported play-count floor.",
    )
    require(
        history["staledatepreserved"],
        "A stale native snapshot overwrote the imported last-played date.",
    )
    require(
        history["freshresetcount"] == 0 and history["freshresetdatecleared"],
        "A fresh native reset was blocked.",
    )
    require(
        history["restoredfloor"] == 10,
        "The production music writer did not restore the fixture import floor.",
    )
    require(
        result["favourites"]["stale_review_rejected"],
        "A stale review erased the native remove/re-add decision.",
    )
    require(
        result["favourites"]["revision_changed"],
        "Native favourite edits did not advance the review revision.",
    )
    require(
        result["favourites"]["fresh_review_accepted"],
        "A current favourite review was incorrectly rejected.",
    )
    require(
        result["scheduler_playlist_recovery"]["scheduler_recovered_without_probe_call"],
        "The plugin's scheduler did not finish the interrupted playlist write.",
    )


def private_playlist(
    listener: Client, viewer: Client, playlist_id: str, user_id: str
) -> list[str]:
    value = listener.request("/Playlists/" + playlist_id)
    require(value["openaccess"] is False, "The fixture playlist became public.")
    require(
        all(
            share["userid"].replace("-", "") == user_id.replace("-", "")
            for share in value["shares"]
        ),
        "The private fixture playlist acquired another user's share.",
    )
    viewer.request("/Playlists/" + playlist_id, expected=(403, 404), raw=True)
    return [entry.replace("-", "").lower() for entry in value["itemids"]]


def public_tracks(admin: Client) -> list[str]:
    items = admin.request(
        "/Items?recursive=true&includeItemTypes=Audio&fields=Path&limit=20"
    )["items"]
    public = [
        item["id"]
        for item in items
        if item.get("path", "").startswith("/media/Public/")
    ]
    require(
        len(public) == 2,
        "The native playlist probe requires two generated public tracks.",
    )
    return public


def playlist_fixture(admin: Client, user: dict) -> tuple[dict, Client]:
    plugin_settings(admin, Enabled=True)
    public = public_tracks(admin)
    prepared = admin.request(
        PROBE + "/Playlist/Prepare", "POST", {"UserId": user["id"], "Items": public}
    )
    original_playlists = admin.request(
        "/Items?recursive=true&includeItemTypes=Playlist&limit=20"
    )["items"]
    admin.request(
        PROBE + "/Playlist/Prepare",
        "POST",
        {"UserId": user["id"], "Items": public},
        expected=409,
        raw=True,
    )
    repeated_playlists = admin.request(
        "/Items?recursive=true&includeItemTypes=Playlist&limit=20"
    )["items"]
    require(
        [item["id"] for item in original_playlists]
        == [item["id"] for item in repeated_playlists],
        "Repeated preparation created another native playlist.",
    )
    return prepared, other_account(admin)


PENDING_PATH = "/JellySin/Lastfm/Me/Playlists/Pending"
RECIPES_PATH = "/JellySin/Lastfm/Me/Playlists"


def interrupted_write(
    admin: Client, listener: Client, viewer: Client, prepared: dict, user: dict
) -> dict:
    """Checks the owner's journal and interrupts the production recovery after the native clear."""
    pending_path = PENDING_PATH
    viewer.request(
        pending_path + "/" + prepared["operationid"], "DELETE", expected=404, raw=True
    )
    pending = listener.request(pending_path)
    require(
        pending["operationid"] == prepared["operationid"],
        "The intended playlist journal was not persisted.",
    )
    require(
        len(private_playlist(listener, viewer, prepared["playlistid"], user["id"]))
        == 2,
        "The initial playlist was incomplete.",
    )
    body = {"UserId": user["id"], "PlaylistId": prepared["playlistid"]}
    interrupted = admin.request(PROBE + "/Playlist/Recover", "POST", body)
    require(
        interrupted["injectedfailure"]
        and interrupted["cleared"]
        and not interrupted["recovered"],
        "The native clear/add fault did not fire.",
    )
    require(
        private_playlist(listener, viewer, prepared["playlistid"], user["id"]) == [],
        "The native partial write was not observed.",
    )
    pending = listener.request(pending_path)
    require(
        pending["status"].startswith("The playlist write did not finish."),
        "Production recovery did not persist the interrupted-write status.",
    )
    return pending


def recovered_after_restart(
    host: DockerHost,
    clients: tuple[Client, Client, Client],
    prepared: dict,
    pending: dict,
    user: dict,
) -> None:
    """Restarts the host, checks the journal survived and runs the explicit production recovery."""
    admin, listener, viewer = clients
    pending_path, recipes_path = PENDING_PATH, RECIPES_PATH
    body = {"UserId": user["id"], "PlaylistId": prepared["playlistid"]}
    host.restart("")
    admin.origin = listener.origin = viewer.origin = host.origin
    admin.request(
        PROBE + "/Playlist/Prepare",
        "POST",
        {"UserId": user["id"], "Items": prepared["intendeditems"]},
        expected=409,
        raw=True,
    )
    persisted = listener.request(pending_path)
    require(
        persisted["operationid"] == prepared["operationid"]
        and persisted["status"] == pending["status"],
        "The journal did not survive restart.",
    )
    require(
        private_playlist(listener, viewer, prepared["playlistid"], user["id"]) == [],
        "The partial playlist did not survive restart.",
    )
    recovered = admin.request(PROBE + "/Playlist/Recover", "POST", body)
    require(
        recovered["recovered"] and not recovered["injectedfailure"],
        "Normal production recovery failed after restart.",
    )
    expected = [item.replace("-", "").lower() for item in prepared["intendeditems"]]
    require(
        private_playlist(listener, viewer, prepared["playlistid"], user["id"])
        == expected,
        "Recovery changed intended native playlist order or contents.",
    )
    require(
        listener.request(pending_path) is None,
        "Completed recovery did not clear its journal.",
    )
    recipes = listener.request(recipes_path)
    require(
        len(recipes) == 1 and recipes[0]["playlistid"] == prepared["playlistid"],
        "Recovery duplicated or lost its playlist recipe.",
    )
    admin.request(PROBE + "/Playlist/Recover", "POST", body)
    require(
        private_playlist(listener, viewer, prepared["playlistid"], user["id"])
        == expected,
        "Completed recovery was not idempotent.",
    )
    require(
        viewer.request(pending_path) is None and viewer.request(recipes_path) == [],
        "Recovery exposed owner state to another user.",
    )


def playlist_checks(
    host: DockerHost, admin: Client, listener: Client, user: dict
) -> dict:
    prepared, viewer = playlist_fixture(admin, user)
    require(
        viewer.request(PENDING_PATH) is None and viewer.request(RECIPES_PATH) == [],
        "Another user saw private pending work.",
    )
    pending = interrupted_write(admin, listener, viewer, prepared, user)
    recovered_after_restart(host, (admin, listener, viewer), prepared, pending, user)
    return {
        "journal_seeded_through_production_store": True,
        "production_recovery_fault_injected_after_native_clear": True,
        "production_failure_status_persisted": True,
        "journal_and_partial_playlist_survived_restart": True,
        "explicit_production_recovery_succeeded": True,
        "intended_order_restored": True,
        "private_owner_isolation": True,
        "pending_cleared": True,
        "recovery_idempotent": True,
        "prepare_retry_rejected_before_side_effects": True,
        "persisted_pending_blocks_prepare_after_restart": True,
    }


def scheduler_checks(host: DockerHost, admin: Client, viewer: Client, listener: Client, user: dict) -> dict:
    """Interrupts a connected user's playlist write, restarts the host and only reads until the plugin's scheduler
    finishes it. Connecting a first account clears the user's stored state, so this user connects before the write."""
    approve(listener)
    prepared = admin.request(
        PROBE + "/Playlist/Prepare",
        "POST",
        {"UserId": user["id"], "Items": public_tracks(admin)},
    )
    interrupted = admin.request(
        PROBE + "/Playlist/Recover",
        "POST",
        {"UserId": user["id"], "PlaylistId": prepared["playlistid"]},
    )
    require(
        interrupted["injectedfailure"] and not interrupted["recovered"],
        "The native fault did not interrupt the connected user's write.",
    )
    pending_path = PENDING_PATH
    require(
        listener.request(pending_path)["operationid"] == prepared["operationid"],
        "The connected user's interrupted write was not pending before the restart.",
    )
    host.restart("")
    admin.origin = listener.origin = viewer.origin = host.origin
    started = time.monotonic()
    # From here the harness makes no recovery call; it only reads.
    while listener.request(pending_path) is not None:
        require(
            time.monotonic() - started < SCHEDULER_SECONDS,
            f"The plugin's scheduler did not finish the interrupted write within {SCHEDULER_SECONDS} seconds of the restart.",
        )
        time.sleep(2)
    seconds = round(time.monotonic() - started, 1)
    expected = [item.replace("-", "").lower() for item in prepared["intendeditems"]]
    require(
        private_playlist(listener, viewer, prepared["playlistid"], user["id"])
        == expected,
        "The scheduler's recovery changed the intended playlist order or contents.",
    )
    return {
        "connected_write_interrupted_before_restart": True,
        "scheduler_recovered_without_probe_call": True,
        "scheduler_recovery_seconds_after_restart": seconds,
        "intended_order_restored": True,
    }


def blocked_refresh_checks(admin: Client, listener: Client, user: dict, stand_in: StandInContainer) -> dict:
    """Refreshes a connected owner's generated playlist while the parental limit blocks every track: the refresh
    reads Last.fm, matches nothing it may write and leaves the playlist as it was; without the limit it writes."""
    viewer = other_account(admin)
    (recipe,) = listener.request(RECIPES_PATH)
    body = {key: recipe[key.lower()] for key in ("Id", "Name", "Source", "Period", "Limit", "DailyRefresh")}
    before = private_playlist(listener, viewer, recipe["playlistid"], user["id"])

    def reads() -> int:
        return sum(line.endswith(" user.getTopTracks") for line in stand_in.lines("calls.log"))

    asked = reads()
    set_policy(admin, user["id"], MaxParentalRating=10, BlockUnratedItems=["Music"])
    listener.request(RECIPES_PATH, "POST", body, expected=409, raw=True)
    require(reads() > asked, "The blocked refresh was refused before it read the owner's Last.fm top tracks.")
    require(listener.request(PENDING_PATH) is None, "The blocked refresh left a pending playlist write.")
    set_policy(admin, user["id"], MaxParentalRating=None, BlockUnratedItems=[])
    require(
        private_playlist(listener, viewer, recipe["playlistid"], user["id"]) == before
        and listener.request(RECIPES_PATH) == [recipe],
        "A refresh under the parental limit changed the playlist or its recipe.",
    )
    refreshed = listener.request(RECIPES_PATH, "POST", body)
    after = private_playlist(listener, viewer, recipe["playlistid"], user["id"])
    require(
        refreshed["playlistid"] == recipe["playlistid"] and len(after) == 1 and after != before,
        "Without the parental limit the same refresh did not rewrite the playlist from Last.fm's top tracks.",
    )
    return {
        "blocked_refresh_read_lastfm": True,
        "blocked_refresh_refused_with_409": True,
        "blocked_refresh_left_no_pending_write": True,
        "playlist_and_recipe_unchanged_after_limit_lifted": True,
        "unblocked_refresh_rewrote_playlist": True,
    }


def run(host: DockerHost, stand_in: StandInContainer) -> dict:
    fixture_music(host.fixture_directory / "Public", "JellySin Extra Fixture")
    match_fixtures(host.fixture_directory)
    host.start()
    admin = Client(host.origin)
    listener, user = fixture_accounts(admin)
    # A made-up application; the server reaches "Last.fm" only through the stand-in.
    admin.request(
        "/JellySin/Lastfm/Admin/Application",
        "PUT",
        {"ApiKey": "a" * 32, "Secret": "b" * 32},
        expected=(200, 204),
    )
    plugins = admin.request("/Plugins")
    diagnostic = [{"id": entry["id"], "status": entry["status"]} for entry in plugins]
    (ROOT / "build" / "native-probe-plugins.json").write_text(
        json.dumps(diagnostic), encoding="utf-8"
    )
    Client(host.origin).request(PROBE, expected=401, raw=True)
    listener.request(PROBE, expected=403, raw=True)
    status = admin.request(PROBE)
    require(
        status["wrapped"],
        "The actual Jellyfin host did not inject CoordinatedUserData.",
    )
    item = fixture_item(admin, user)
    library = match_checks(admin, user)
    history = history_checks(admin, listener, item)
    (ROOT / "build" / "native-probe-history.json").write_text(
        json.dumps(history, indent=2), encoding="utf-8"
    )
    favourites = favourite_checks(admin, listener, item)
    playlist = playlist_checks(host, admin, listener, user)
    connected, connected_user = probe_user(admin)
    scheduler = scheduler_checks(host, admin, other_account(admin), connected, connected_user)
    listener.origin = host.origin
    limited = limited_owner_checks(host.version, admin, listener, user)
    blocked_refresh = blocked_refresh_checks(admin, connected, connected_user, stand_in)
    delivery = listener.request("/JellySin/Lastfm/Me/Delivery")
    require(
        delivery["pending"] == 0,
        "An unconnected fixture unexpectedly queued a Last.fm delivery.",
    )
    return {
        "native_di": status,
        "history": history,
        "favourites": favourites,
        "playlist": playlist,
        "scheduler_playlist_recovery": scheduler,
        "library_matching": library,
        "parental_limit_playlist": limited,
        "parental_limit_refresh": blocked_refresh,
        "no_lastfm_deliveries": True,
    }


def arguments() -> tuple[argparse.Namespace, str]:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plugin", required=True, type=Path)
    parser.add_argument("--probe", required=True, type=Path)
    add_server_options(parser)
    parser.add_argument(
        "--report", type=Path, default=ROOT / "build" / "native-userdata-report.json"
    )
    args = parser.parse_args()
    for path in (args.plugin, args.probe):
        require(
            path.is_file()
            and not path.is_symlink()
            and 0 < path.stat().st_size <= 64 * 1024 * 1024,
            "A plugin input is missing, unsafe or exceeds 64 MiB.",
        )
    return args, chosen_image(parser, args)


def main() -> int:
    args, image = arguments()
    directory = tempfile.TemporaryDirectory(prefix="jellysin-native-probe-")
    (Path(directory.name) / "stand-in").mkdir(mode=0o755)
    stand_in = StandInContainer(Path(directory.name) / "stand-in")
    host = DockerHost(
        args.plugin.resolve(),
        image,
        probe_plugin=args.probe.resolve(),
        extra_arguments=stand_in.server_arguments(),
    )
    (ROOT / "build").mkdir(exist_ok=True)
    try:
        stand_in.start()
        result = run(host, stand_in)
        result["artifacts"] = {
            "image": image,
            "server": host.version,
            "production_sha256": hashlib.sha256(args.plugin.read_bytes()).hexdigest(),
            "fixture_sha256": hashlib.sha256(args.probe.read_bytes()).hexdigest(),
        }
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
        validate(result)
        print(
            "PASS: native DI, stale import protection, intentional resets, favourite revisions and interrupted playlist recovery."
        )
        print(
            "PASS: the plugin's scheduler finished an interrupted playlist write after a restart without a probe call."
        )
        print(
            "PASS: library matching and generated playlist visibility under a parental limit."
        )
        print(
            "PASS: a playlist refresh under a parental limit that blocks every track wrote nothing."
        )
        return 0
    except SmokeFailure:
        if host.started:
            (ROOT / "build" / "native-probe-host.log").write_text(
                docker("logs", "--tail", "1000", host.name), encoding="utf-8"
            )
        raise
    finally:
        try:
            host.cleanup()
        finally:
            stand_in.cleanup()
            directory.cleanup()


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (SmokeFailure, OSError, ValueError, KeyError) as error:
        print(f"Native userdata probe failed: {error}", file=sys.stderr)
        sys.exit(1)
