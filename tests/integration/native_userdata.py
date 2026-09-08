#!/usr/bin/env python3
"""Run test-only userdata probes on a new disposable Jellyfin 12 Docker host."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import secrets
import sys
import uuid

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
from smoke import (  # noqa: E402
    Client,
    DEFAULT_IMAGE,
    DockerHost,
    SmokeFailure,
    add_library,
    docker,
    fixture_music,
    plugin_settings,
    require,
    setup_server,
    wait_music,
)

PROBE = "/JellySin/NativeProbe"


def fixture_accounts(admin: Client) -> tuple[Client, dict]:
    setup_server(admin, secrets.token_urlsafe(24))
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
    plugin_settings(
        admin, Enabled=False, MetadataEnabled=False, SimilarityEnabled=False
    )
    for name in ("Public", "Hidden"):
        add_library(admin, name)
    admin.request("/Library/Refresh", "POST", expected=204)
    items = wait_music(admin)
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


def playlist_fixture(admin: Client, user: dict) -> tuple[dict, Client]:
    plugin_settings(admin, Enabled=True, MetadataEnabled=False, SimilarityEnabled=False)
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
    password = secrets.token_urlsafe(24)
    other = admin.request(
        "/Users/New",
        "POST",
        {"Name": "JellySin Probe Other " + uuid.uuid4().hex[:8], "Password": password},
    )
    require(
        not other["policy"]["isadministrator"],
        "The other fixture user unexpectedly has administrator access.",
    )
    viewer = Client(admin.origin)
    viewer.authenticate(other["name"], password)
    return prepared, viewer


def playlist_checks(
    host: DockerHost, admin: Client, listener: Client, user: dict
) -> dict:
    prepared, viewer = playlist_fixture(admin, user)
    pending_path = "/JellySin/Lastfm/Me/Playlists/Pending"
    recipes_path = "/JellySin/Lastfm/Me/Playlists"
    require(
        viewer.request(pending_path) is None and viewer.request(recipes_path) == [],
        "Another user saw private pending work.",
    )
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


def run(host: DockerHost) -> dict:
    fixture_music(host.fixture_directory / "Public", "JellySin Extra Fixture")
    host.start()
    admin = Client(host.origin)
    listener, user = fixture_accounts(admin)
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
    history = history_checks(admin, listener, item)
    (ROOT / "build" / "native-probe-history.json").write_text(
        json.dumps(history, indent=2), encoding="utf-8"
    )
    favourites = favourite_checks(admin, listener, item)
    playlist = playlist_checks(host, admin, listener, user)
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
        "no_lastfm_deliveries": True,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plugin", required=True, type=Path)
    parser.add_argument("--probe", required=True, type=Path)
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
    host = DockerHost(
        args.plugin.resolve(), DEFAULT_IMAGE, probe_plugin=args.probe.resolve()
    )
    (ROOT / "build").mkdir(exist_ok=True)
    try:
        result = run(host)
        result["artifacts"] = {
            "image": DEFAULT_IMAGE,
            "production_sha256": hashlib.sha256(args.plugin.read_bytes()).hexdigest(),
            "fixture_sha256": hashlib.sha256(args.probe.read_bytes()).hexdigest(),
        }
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
        validate(result)
        print(
            "PASS: native DI, stale import protection, intentional resets, favourite revisions and interrupted playlist recovery."
        )
        return 0
    except SmokeFailure:
        if host.started:
            (ROOT / "build" / "native-probe-host.log").write_text(
                docker("logs", "--tail", "1000", host.name), encoding="utf-8"
            )
        raise
    finally:
        host.cleanup()


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (SmokeFailure, OSError, ValueError, KeyError) as error:
        print(f"Native userdata probe failed: {error}", file=sys.stderr)
        sys.exit(1)
