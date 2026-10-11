"""Library matching and parental-limit playlist checks for the native probe driver."""

from __future__ import annotations

import json
from pathlib import Path, PurePosixPath
import secrets
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
from smoke import Client, SmokeFailure, fixture_music, require  # noqa: E402

MATCH = "/JellySin/NativeProbe/Library/Match"
RECIPES = "/JellySin/Lastfm/Me/Playlists"
LIBRARY = "Match"
PUBLIC = "JellySin Public Fixture"
KINDS = {"artist": "MusicArtist", "album": "MusicAlbum"}
ABSENT = "no such library item"
# File name, title, artist and album of each track in the matching library.
FIXTURES = (
    ("joga", "Jóga", "Björk", "Homogenic"),
    ("thunder", "Thunderstruck", "AC/DC", "The Razors Edge"),
    ("tribute", "Thunderstruck", "Thunder Tribute", "Tribute"),
)
# Artist, title and album as Last.fm names them, then the file that has to be matched (None: no match).
TRACKS = (
    (("Björk", "Jóga", None), "joga"),
    (("björk", "jóga", None), "joga"),
    (("Björk", "Jóga", "Homogenic"), "joga"),
    (("Björk", "Jóga", "Post"), None),
    (("Bjork", "Joga", None), None),
    (("AC/DC", "Thunderstruck", None), "thunder"),
    (("Thunder Tribute", "Thunderstruck", None), "tribute"),
    (("AC DC", "Thunderstruck", None), None),
    (("Nobody", "Thunderstruck", None), None),
    (("JellySin Fixture Artist", PUBLIC, None), PUBLIC),
)
# Kind, name and album artist, then the name of the library item that has to be matched (None: no match).
ENTITIES = (
    (("artist", "Björk", None), "Björk"),
    (("artist", "AC/DC", None), "AC/DC"),
    (("artist", "Nobody", None), None),
    (("album", "Homogenic", "Björk"), "Homogenic"),
    (("album", "homogenic", "björk"), "Homogenic"),
    (("album", "Homogenic", "Bjork"), None),
    (("album", "The Razors Edge", "AC/DC"), "The Razors Edge"),
    (("album", "Tribute", "Thunder Tribute"), "Tribute"),
    (("album", "Tribute", "AC/DC"), None),
)


def plain(identifier: str | None) -> str | None:
    return identifier.replace("-", "").lower() if identifier else None


def hides_blocked_playlists(version: str) -> bool:
    """Jellyfin 12.1 and later hide a playlist from an owner whose parental limit blocks every track in it."""
    major, minor = (int(part) for part in version.split(".")[:2])
    return (major, minor) >= (12, 1)


def differences(expected: list[tuple[str, str | None]], outcomes: list[dict]) -> list[str]:
    """Return every query, given as (label, item or None), that the library answered differently."""
    wrong = []
    for (label, item), outcome in zip(expected, outcomes, strict=True):
        found = plain(outcome.get("itemid"))
        status = outcome.get("status", "matched" if found else "missing")
        if found != plain(item) or status != ("matched" if item else "missing"):
            wrong.append(f"{label} ({status})")
    return wrong


def match_fixtures(directory: Path) -> None:
    for name, title, artist, album in FIXTURES:
        folder = directory / LIBRARY / ("artist-" + name) / ("album-" + name)
        fixture_music(folder, title, artist, album, name)


def wait_scan(admin: Client) -> None:
    """Albums and artists appear after the tracks, so wait for the scan itself."""
    for _ in range(120):
        tasks = [task for task in admin.request("/ScheduledTasks") if task["key"] == "RefreshLibrary"]
        require(len(tasks) == 1, "The server lists no single library scan task.")
        if tasks[0]["state"] == "Idle":
            return
        time.sleep(0.5)
    raise SmokeFailure("The fixture library scan did not finish within 60 seconds.")


def other_account(admin: Client) -> Client:
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
    return viewer


def expected_matches(admin: Client) -> tuple[list, list]:
    listing = "/Items?recursive=true&fields=Path&limit=40&includeItemTypes="
    files = {PurePosixPath(item["path"]).stem: item["id"] for item in admin.request(listing + "Audio")["items"]}
    named = {(item["type"], item["name"]): item["id"] for item in admin.request(listing + "MusicArtist,MusicAlbum")["items"]}
    tracks = [(" / ".join(filter(None, query)), files.get(file, ABSENT) if file else None) for query, file in TRACKS]
    entities = [
        (" / ".join(filter(None, query)), named.get((KINDS[query[0]], name), ABSENT) if name else None)
        for query, name in ENTITIES
    ]
    return tracks, entities


def match_checks(admin: Client, user: dict) -> dict:
    tracks, entities = expected_matches(admin)
    body = {
        "UserId": user["id"],
        "Tracks": [{"Artist": artist, "Title": title, "Album": album} for (artist, title, album), _ in TRACKS],
        "Entities": [{"Kind": kind, "Name": name, "Artist": artist} for (kind, name, artist), _ in ENTITIES],
    }
    found = admin.request(MATCH, "POST", body)
    wrong = differences(tracks, found["tracks"]) + differences(entities, found["entities"])
    require(not wrong, "Library matching differs from the recorded result for: " + "; ".join(wrong))
    return {"track_queries": len(tracks), "entity_queries": len(entities)}


def set_policy(admin: Client, user_id: str, **changes: object) -> None:
    payload, _, _ = admin.request("/Users/" + user_id, raw=True)
    policy = json.loads(payload)["Policy"]
    policy.update(changes)
    admin.request("/Users/" + user_id + "/Policy", "POST", policy, expected=204)


def owner_view(admin: Client, listener: Client, viewer: Client, user: dict) -> dict:
    """Return what the owner can reach of the generated playlist, natively and through the plugin."""
    (recipe,) = listener.request(RECIPES)
    playlist = plain(recipe["playlistid"])
    viewer.request("/Playlists/" + playlist, expected=(403, 404), raw=True)
    require(viewer.request(RECIPES) == [], "Another user saw the owner's playlist recipe.")
    listed = listener.request("/Items?recursive=true&includeItemTypes=Playlist&limit=20")["items"]
    payload, _, status = listener.request(f"/Playlists/{playlist}/Items", expected=(200, 404), raw=True)
    _, _, direct = listener.request("/Items/" + playlist, expected=(200, 404), raw=True)
    query = {"UserId": user["id"], "Tracks": [{"Artist": "JellySin Fixture Artist", "Title": PUBLIC}], "Entities": []}
    return {
        "playlist": playlist,
        "listed": any(plain(item["id"]) == playlist for item in listed),
        "direct": direct,
        "tracks": [plain(item["Id"]) for item in json.loads(payload)["Items"]] if status == 200 else None,
        "match": admin.request(MATCH, "POST", query)["tracks"][0]["status"],
    }


def limited_owner_checks(version: str, admin: Client, listener: Client, user: dict) -> dict:
    viewer = other_account(admin)
    unlimited = owner_view(admin, listener, viewer, user)
    require(
        unlimited["listed"] and unlimited["direct"] == 200 and len(unlimited["tracks"]) == 2 and unlimited["match"] == "matched",
        "The owner cannot reach the generated playlist before any parental limit.",
    )
    set_policy(admin, user["id"], MaxParentalRating=10)
    require(
        owner_view(admin, listener, viewer, user) == unlimited,
        "A parental limit that allows the tracks changed the owner's generated playlist.",
    )
    set_policy(admin, user["id"], MaxParentalRating=10, BlockUnratedItems=["Music"])
    blocked = owner_view(admin, listener, viewer, user)
    require(
        not blocked["tracks"] and blocked["match"] == "missing",
        "A track blocked by the owner's parental limit stayed readable or matchable.",
    )
    hidden = hides_blocked_playlists(version)
    require(
        blocked["listed"] == (not hidden) and blocked["direct"] == (404 if hidden else 200),
        f"Jellyfin {version} does not treat a fully blocked playlist the way its release is recorded to.",
    )
    require(blocked["playlist"] == unlimited["playlist"], "The playlist recipe changed while its tracks were blocked.")
    set_policy(admin, user["id"], MaxParentalRating=None, BlockUnratedItems=[])
    require(
        owner_view(admin, listener, viewer, user) == unlimited,
        "Lifting the parental limit did not return the generated playlist unchanged.",
    )
    return {
        "allowing_limit_keeps_playlist_and_tracks": True,
        "blocked_tracks_unreadable_and_unmatched": True,
        "server_hides_fully_blocked_playlist": hidden,
        "recipe_kept_while_blocked": True,
        "unchanged_after_limit_lifted": True,
        "other_user_never_sees_playlist_or_recipe": True,
    }
