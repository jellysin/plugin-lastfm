"""Scrobble delivery under library load: connected accounts play to the scrobble threshold against the Last.fm stand-in.

tools/library_load.py runs this while its search and playback clients load the server. Each account signs in to a
Last.fm user of its own at the stand-in (tests/integration/lastfm_stand_in.py), and every account plays the same
tracks, so only the session a scrobble carries tells the accounts apart. The run has three periods:

- open: every account plays the first track, and the stand-in must receive it from every account.
- refused: the stand-in answers scrobbles with the retryable error 16 while every account plays the second track.
  Each account must keep that listen queued. The stand-in then recovers, and the listen must arrive.
- silent: one account plays the third track as a player that reports only the start and the stop. The server's own
  progress ticks stand in for the missing reports, and the stand-in must receive that listen once.

The check passes when the stand-in received every listen of every account exactly once and no listen for an
account that did not play it. No real Last.fm account, application or credential takes part.
"""

from __future__ import annotations

from collections import Counter
from collections.abc import Callable
from concurrent.futures import Executor
from pathlib import Path
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests" / "integration"))
from lastfm_stand_in import APPROVAL_TOKEN, LASTFM_USER  # noqa: E402
from page_check import StandInContainer  # noqa: E402
from smoke import PREFIX, Client, SmokeFailure, create_user, fixture_music, require  # noqa: E402
from state_carryover import approve  # noqa: E402

ACCOUNTS = ("one", "two", "three", "four")
ARTIST = "JellySin Delivery Artist"
TITLES = ("JellySin Delivery Open", "JellySin Delivery Refused", "JellySin Delivery Silent")
# The account whose player reports no progress of its own in the silent period.
SILENT = ACCOUNTS[0]
# Longer than the 30 seconds Last.fm requires; a listen counts once half of it was heard.
TRACK_SECONDS = 40
PLAYED_SECONDS = 24
PROGRESS_SECONDS = 4
# The silent play spans more than the 45 seconds one report may credit, so only the server's own ticks can carry it
# past half of its longer track.
SILENT_TRACK_SECONDS = 100
SILENT_PLAYED_SECONDS = 56
TICKS_PER_SECOND = 10_000_000
# A scrobble is sent when it is stored; a refused one waits 15 to 30 seconds, then for the one-minute retry task.
ARRIVAL_SECONDS = 120
RECOVERY_SECONDS = 600
TAGS_SECONDS = 1200


def fixtures(root: Path) -> None:
    """The delivery tracks, beside the generated library in its Load folder."""
    for title in TITLES:
        seconds = SILENT_TRACK_SECONDS if title == TITLES[2] else TRACK_SECONDS
        fixture_music(root / "Load" / "Delivery", title, ARTIST, "Delivery Album", seconds=seconds)


def items(admin: Client) -> dict[str, str]:
    """The library id of each delivery track, once the scan has read its artist; the scan counts a track before its tags."""
    deadline, found = time.monotonic() + TAGS_SECONDS, []
    while time.monotonic() < deadline:
        found = admin.request("/Items?recursive=true&includeItemTypes=Audio&fields=Artists&searchTerm=JellySin%20Delivery")["items"]
        ready = {item["name"]: item["id"] for item in found if item.get("artists") == [ARTIST]}
        if set(ready) >= set(TITLES):
            return ready
        time.sleep(2)
    raise SmokeFailure(f"The delivery tracks did not carry their artist {TAGS_SECONDS} seconds after the scan: "
                       f"{[(item.get('name'), item.get('artists')) for item in found]}")


def connect(admin: Client, password: str) -> dict[str, Client]:
    """One ordinary user per account, each connected to a Last.fm user of its own at the stand-in."""
    clients = {}
    for label in ACCOUNTS:
        create_user(admin, password, "Delivery " + label)
        client = Client(admin.origin)
        client.authenticate("Delivery " + label, password)
        approve(client, f"{APPROVAL_TOKEN}-{label}", f"{LASTFM_USER}-{label}")
        clients[label] = client
    return clients


def play(client: Client, item: str, progress: bool = True, seconds: int = PLAYED_SECONDS) -> None:
    """A player that plays in real time: its reported position never runs ahead of the clock. Without progress it
    reports only the start and the stop."""
    playing = {"ItemId": item, "MediaSourceId": item, "PlaySessionId": uuid.uuid4().hex, "PositionTicks": 0,
               "IsPaused": False, "PlayMethod": "DirectPlay", "CanSeek": True}
    started, position = time.monotonic(), 0
    client.request("/Sessions/Playing", "POST", playing, 204)
    while position < seconds:
        time.sleep(PROGRESS_SECONDS)
        position = int(time.monotonic() - started)
        if progress:
            client.request("/Sessions/Playing/Progress", "POST", {**playing, "PositionTicks": position * TICKS_PER_SECOND, "EventName": "TimeUpdate"}, 204)
    client.request("/Sessions/Playing/Stopped", "POST", {**playing, "PositionTicks": position * TICKS_PER_SECOND}, 204)


def scrobbles(lines: list[str], refused: bool = False) -> list[tuple[str, list[str]]]:
    """Each scrobble request as its account label and its "artist - title" tracks: accepted ones, or refused ones when refused."""
    found = []
    for line in lines:
        head, *tracks = line.split(" | ")
        words = head.split(" ")
        if refused != (words[0] == "REFUSED"):
            continue
        words = words[1:] if refused else words
        if words[:2] == ["POST", "track.scrobble"]:
            found.append((words[3] if words[2:3] == ["as"] and len(words) == 4 else "", tracks))
    return found


def received(lines: list[str], refused: bool = False) -> Counter[tuple[str, str]]:
    """Each scrobbled track by account label and "artist - title"."""
    return Counter((label, track) for label, tracks in scrobbles(lines, refused) for track in tracks)


def answered(lines: list[str], refused: bool = False) -> set[str]:
    """The accounts with at least one scrobble request, whatever it carried."""
    return {label for label, _ in scrobbles(lines, refused)}


def listens(title: str, accounts: tuple[str, ...] = ACCOUNTS) -> Counter[tuple[str, str]]:
    return Counter((label, f"{ARTIST} - {title}") for label in accounts)


def verdict(expected: Counter[tuple[str, str]], got: Counter[tuple[str, str]]) -> None:
    """Refuses a missing listen, a listen received twice and a listen for an account that did not play it."""
    missing, extra = expected - got, got - expected
    require(not missing and not extra,
            f"The stand-in received the listens wrongly: missing {sorted(missing)}; repeated or for another account {sorted(extra)}.")


def wait(condition: Callable[[], bool], seconds: float, message: str) -> float:
    started = time.monotonic()
    while not condition():
        require(time.monotonic() - started < seconds, message)
        time.sleep(1)
    return time.monotonic() - started


def period(executor: Executor, clients: dict[str, Client], item: str) -> None:
    """Every account plays the item at the same time."""
    for future in [executor.submit(play, client, item) for client in clients.values()]:
        future.result(timeout=PLAYED_SECONDS + 60)


def pending(clients: dict[str, Client]) -> list[int]:
    return [client.request(PREFIX + "/Me/Delivery")["pending"] for client in clients.values()]


def deliver(executor: Executor, stand_in: StandInContainer, clients: dict[str, Client], ids: dict[str, str]) -> dict:
    """Runs the open, the refused and the silent period and checks what the stand-in received."""
    def calls() -> list[str]:
        return stand_in.lines("calls.log")

    period(executor, clients, ids[TITLES[0]])
    wait(lambda: answered(calls()) >= set(ACCOUNTS) and not any(pending(clients)), ARRIVAL_SECONDS,
         "An account sent no scrobble for the open period, or kept it queued.")
    verdict(listens(TITLES[0]), received(calls()))
    refusal = stand_in.directory / "refuse.txt"
    refusal.write_text("error 16\n", encoding="utf-8")
    period(executor, clients, ids[TITLES[1]])
    wait(lambda: answered(calls(), refused=True) >= set(ACCOUNTS) and min(pending(clients)) >= 1, ARRIVAL_SECONDS,
         "An account did not attempt its listen of the refused period, or did not keep it queued.")
    verdict(listens(TITLES[0]), received(calls()))
    refusal.unlink()
    expected = listens(TITLES[0]) + listens(TITLES[1])
    recovered = wait(lambda: not expected - received(calls()) and not any(pending(clients)), RECOVERY_SECONDS,
                     f"The refused listens were not all delivered within {RECOVERY_SECONDS} seconds of the recovery: "
                     f"missing {sorted(expected - received(calls()))}, pending {pending(clients)}.")
    verdict(expected, received(calls()))
    play(clients[SILENT], ids[TITLES[2]], progress=False, seconds=SILENT_PLAYED_SECONDS)
    expected += listens(TITLES[2], (SILENT,))
    wait(lambda: not expected - received(calls()) and not any(pending(clients)), ARRIVAL_SECONDS,
         f"The listen of a player that reports only its start and stop did not arrive: missing {sorted(expected - received(calls()))}.")
    verdict(expected, received(calls()))
    return {"accounts": len(clients), "listens": sum(expected.values()), "refusedAttempts": len(scrobbles(calls(), refused=True)),
            "secondsToRecover": round(recovered, 1), "lastfmCalls": len(calls())}
