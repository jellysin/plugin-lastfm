#!/usr/bin/env python3
"""Time favourite synchronization on a real synthetic library against a Last.fm stand-in with a large loved collection.

The library is the one tools/library_load.py generates. The stand-in of the page check serves every generated
track but the last as loved, 200 to a page, under a made-up application; no real Last.fm account, application or
credential is used. One ordinary user connects and the run records three favourite runs:

- initial: favourites are turned on and synchronized again until the reconciliation completes. A run works
  through at most 200 tracks, so 10,000 loved tracks take about 50 runs; every loved track becomes a favourite.
- unchanged: a synchronization with nothing changed on either side, again until the reconciliation completes.
- one favourite: the last generated track is marked a favourite in Jellyfin and synchronized again until it is loved,
  then until the reconciliation completes. A run reconciles the next 200 track ids in id order, so the change waits
  for the run whose window holds it; secondsToChange records when the love reached the stand-in.
- one removal: the stand-in stops serving the oldest loved track and the user synchronizes until that track is proposed
  for removal from Jellyfin, then until the reconciliation completes; secondsToChange records when the proposal appeared.
  The review then applies the proposal, and the run records how long that request took and the loved pages it read.

Each phase records its wall time, its runs and how many loved-collection pages it read from the stand-in.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import secrets
import sys
import tempfile
import time
from typing import Any
import urllib.error

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tests" / "integration"))
from library_load import fixtures, scan  # noqa: E402
from page_check import StandInContainer  # noqa: E402
from smoke import PREFIX, Client, DockerHost, SmokeFailure, add_server_options, chosen_image, create_user, require, setup_server  # noqa: E402
from state_carryover import approve  # noqa: E402

# One favourite run reads the whole loved collection; this bounds each run, not the phase.
RUN_SECONDS = 1200
# A run that leaves work for the next one says so in this status.
UNFINISHED = "Processed 200 tracks."


def described(admin: Client, count: int) -> float:
    """Waits until every generated track carries its artist and album; the scan counts a track before it reads its tags."""
    started, ready = time.monotonic(), 0
    while time.monotonic() - started < RUN_SECONDS:
        ready = 0
        for offset in range(0, count, 2000):
            items = admin.request(f"/Items?recursive=true&includeItemTypes=Audio&fields=Artists&startIndex={offset}&limit=2000")["items"]
            ready += sum(1 for item in items if item.get("artists") and item.get("album") == "Load Album")
        if ready == count:
            return time.monotonic() - started
        time.sleep(5)
    raise SmokeFailure(f"{ready} of {count} tracks carried their tags after {RUN_SECONDS} seconds.")


def loved_reads(stand_in: StandInContainer) -> int:
    return sum(1 for line in stand_in.lines("calls.log") if line.split(" ")[1:2] == ["user.getLovedTracks"])


def settle(listener: Client, review: dict[str, Any], started: float) -> dict[str, Any]:
    """Waits until one favourite run, started at started, is no longer running."""
    while review["running"]:
        require(time.monotonic() - started < RUN_SECONDS, f"The favourite run did not finish within {RUN_SECONDS} seconds.")
        time.sleep(0.5)
        review = listener.request(PREFIX + "/Me/Favourites")
    return review


def favourites(listener: Client) -> int:
    return listener.request("/Items?recursive=true&includeItemTypes=Audio&isFavorite=true&limit=0&enableTotalRecordCount=true")["totalrecordcount"]


def timed(stand_in: StandInContainer, listener: Client, name: str, start: Any, done: Any = None, limit: int = 1) -> dict[str, Any]:
    """Starts a phase, waits for done when given, and synchronizes again while a run leaves work, at most limit runs."""
    reads, started, runs, reached = loved_reads(stand_in), time.monotonic(), 1, None
    review = start()
    if done:
        # A run reconciles the next 200 track ids in id order, so the changed track waits for the run whose window holds it;
        # synchronize again, as background refresh would every five minutes, until the change reaches the stand-in.
        for _ in range(limit):
            waited = time.monotonic()
            # The change worker waits two seconds before its run starts; a run that is not running after three has ended.
            while not done() and (time.monotonic() - waited < 3 or listener.request(PREFIX + "/Me/Favourites")["running"]):
                require(time.monotonic() - waited < RUN_SECONDS, f"The {name} run did not finish within {RUN_SECONDS} seconds.")
                time.sleep(0.2)
            if done():
                reached = round(time.monotonic() - started, 2)
                break
            if runs == limit:
                break
            listener.request(PREFIX + "/Me/Favourites/Sync", "POST")
            runs += 1
        require(reached is not None, f"The {name} change did not reach the stand-in within {limit} runs.")
    review = settle(listener, listener.request(PREFIX + "/Me/Favourites"), time.monotonic())
    while (review.get("status") or "").startswith(UNFINISHED):
        require(runs < limit, f"The {name} phase did not complete within {limit} runs.")
        review, runs = settle(listener, listener.request(PREFIX + "/Me/Favourites/Sync", "POST"), time.monotonic()), runs + 1
    result = {"run": name, "seconds": round(time.monotonic() - started, 2), "runs": runs, "lovedPageReads": loved_reads(stand_in) - reads,
              "status": review.get("status"), "pendingRemovals": len(review.get("pending") or []), "libraryFavourites": favourites(listener)}
    return result | ({"secondsToChange": reached} if done else {})


def removal(stand_in: StandInContainer, listener: Client, count: int, limit: int) -> dict[str, Any]:
    """Drops the oldest loved track at the stand-in, synchronizes until its removal is proposed, then times the review."""
    title = f"JellySin Load {count - 2:06d}"

    def proposal() -> dict[str, Any] | None:
        return next((entry for entry in listener.request(PREFIX + "/Me/Favourites")["pending"] if entry["track"]["title"] == title), None)

    def drop() -> dict[str, Any]:
        written = stand_in.directory / "loved-count.tmp"
        written.write_text(f"{count - 2}\n", encoding="utf-8")
        os.replace(written, stand_in.directory / "loved-count.txt")
        return listener.request(PREFIX + "/Me/Favourites/Sync", "POST")

    run = timed(stand_in, listener, "one removal", drop, lambda: proposal() is not None, limit)
    entry = proposal()
    require(entry is not None and entry["removefrom"] == "jellyfin", f"The removal of {title} is no longer proposed after the run: {entry}")
    reads, before, started = loved_reads(stand_in), run["libraryFavourites"], time.monotonic()
    listener.request(PREFIX + "/Me/Favourites/Review", "POST", {"ReviewId": entry["id"], "Apply": True}, timeout=RUN_SECONDS)
    seconds, after = round(time.monotonic() - started, 2), favourites(listener)
    require(after == before - 1 and proposal() is None, f"The review left {after} of {before} favourites and the proposal {proposal()}.")
    return run | {"reviewSeconds": seconds, "reviewLovedPageReads": loved_reads(stand_in) - reads, "libraryFavourites": after}


def exercise(host: DockerHost, stand_in: StandInContainer, count: int) -> dict[str, Any]:
    fixtures(host.fixture_directory, count)
    host.start()
    admin = Client(host.origin)
    password = secrets.token_urlsafe(24)
    setup_server(admin, secrets.token_urlsafe(24))
    print(f"Scanning {count} generated tracks with internet providers disabled.", flush=True)
    scan_seconds, _ = scan(admin, count)
    scan_seconds += described(admin, count)
    admin.request(PREFIX + "/Admin/Application", "PUT", {"ApiKey": "a" * 32, "Secret": "b" * 32}, expected=(200, 204))
    create_user(admin, password)
    listener = Client(host.origin)
    listener.authenticate("SmokeListener", password)
    approve(listener)
    # A run reconciles at most 200 tracks, so every phase over the whole collection takes about count / 200 runs.
    limit = count // 200 + 5
    runs = [timed(stand_in, listener, "initial", lambda: listener.request(PREFIX + "/Me/Favourites", "PUT", {"Enabled": True}), limit=limit)]
    require(runs[0]["libraryFavourites"] == count - 1, f"The initial phase marked {runs[0]['libraryFavourites']} of {count - 1} loved tracks in {runs[0]['runs']} runs; last status: {runs[0]['status']}")
    runs.append(timed(stand_in, listener, "unchanged", lambda: listener.request(PREFIX + "/Me/Favourites/Sync", "POST"), limit=limit))
    title, artist = f"JellySin Load {count - 1:06d}", f"Artist {(count - 1) // 100:04d}"
    item = listener.request("/Items?recursive=true&includeItemTypes=Audio&searchTerm=" + title.replace(" ", "%20"))["items"][0]["id"]
    love = f"POST track.love | {artist} - {title}"

    def mark() -> dict[str, Any]:
        require(listener.request("/UserFavoriteItems/" + item, "POST")["isfavorite"], "The native favourite was not saved.")
        return {"running": True}

    runs.append(timed(stand_in, listener, "one favourite", mark, lambda: love in stand_in.lines("calls.log"), limit))
    runs.append(removal(stand_in, listener, count, limit))
    for run in runs[1:3]:
        require(run["pendingRemovals"] == 0, f"The {run['run']} run proposed removals from an unchanged collection.")
    return {"image": host.image, "server": host.version, "libraryTracks": count, "lovedTracks": count - 1,
            "lovedPageSize": 200, "scanSeconds": round(scan_seconds, 1), "containerCpus": 2, "containerMemoryMiB": 1536, "runs": runs}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plugin", type=Path, required=True)
    add_server_options(parser)
    parser.add_argument("--tracks", type=int, default=10000, help="Generated library size, from 1000 to 20000 tracks.")
    parser.add_argument("--report", type=Path, default=Path("build/favourites-load.json"))
    args = parser.parse_args()
    require(1000 <= args.tracks <= 20000, "Library size must be between 1000 and 20000 tracks.")
    with tempfile.TemporaryDirectory(prefix="jellysin-favourites-load-") as directory:
        stand_in = StandInContainer(Path(directory))
        (Path(directory) / "loved-count.txt").write_text(f"{args.tracks - 1}\n", encoding="utf-8")
        host = None
        try:
            stand_in.start()
            host = DockerHost(args.plugin.resolve(strict=True), chosen_image(parser, args), extra_arguments=stand_in.server_arguments())
            report = exercise(host, stand_in, args.tracks)
            args.report.parent.mkdir(parents=True, exist_ok=True)
            args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
            for run in report["runs"]:
                print(f"{run['run']}: {run['seconds']} s, {run['runs']} runs, {run['lovedPageReads']} loved pages read", flush=True)
                if "reviewSeconds" in run:
                    print(f"review: {run['reviewSeconds']} s, {run['reviewLovedPageReads']} loved pages read", flush=True)
            return 0
        finally:
            try:
                if host:
                    host.cleanup()
            finally:
                stand_in.cleanup()


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (SmokeFailure, OSError, ValueError, KeyError, urllib.error.URLError) as error:
        raise SystemExit(str(error) if isinstance(error, SmokeFailure) else "Favourite load check failed: " + type(error).__name__) from None
