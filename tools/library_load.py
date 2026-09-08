#!/usr/bin/env python3
"""Measure bounded plugin search and playback HTTP requests on a real synthetic music library."""

from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import secrets
import struct
import time
import urllib.error
import urllib.parse
import uuid
import wave

from smoke import Client, DEFAULT_IMAGE, DockerHost, PREFIX, SmokeFailure, add_library, create_user, plugin_settings, require, setup_server


def fixtures(root: Path, count: int) -> None:
    for index in range(count):
        directory = root / "Load" / f"Artist {index // 100:04d}"
        directory.mkdir(parents=True, exist_ok=True)
        directory.chmod(0o755)
        title = f"JellySin Load {index:06d}"
        path = directory / (title + ".wav")
        with wave.open(str(path), "wb") as audio:
            audio.setnchannels(1)
            audio.setsampwidth(1)
            audio.setframerate(8000)
            audio.writeframes(b"\x80" * 8000)
        tags = b"INFO"
        for name, value in ((b"INAM", title), (b"IART", directory.name), (b"IPRD", "Load Album")):
            encoded = value.encode() + b"\0"
            tags += name + struct.pack("<I", len(encoded)) + encoded + (b"\0" if len(encoded) % 2 else b"")
        with path.open("r+b") as audio:
            audio.seek(0, 2)
            audio.write(b"LIST" + struct.pack("<I", len(tags)) + tags)
            size = audio.tell()
            audio.seek(4)
            audio.write(struct.pack("<I", size - 8))
        path.chmod(0o644)
    (root / "Load").chmod(0o755)


def scan(admin: Client, count: int) -> tuple[float, str]:
    add_library(admin, "Load")
    started = time.monotonic()
    admin.request("/Library/Refresh", "POST", expected=204)
    deadline = started + 1200
    while time.monotonic() < deadline:
        response = admin.request("/Items?recursive=true&includeItemTypes=Audio&limit=1&enableTotalRecordCount=true")
        if response["totalrecordcount"] == count:
            return time.monotonic() - started, response["items"][0]["id"]
        time.sleep(2)
    raise SmokeFailure("The real library did not scan every generated track within 20 minutes.")


def request_workload(origin: str, token: str, item: str, worker: int, count: int) -> tuple[str, list[float]]:
    client = Client(origin, token=token)
    playback = worker % 2 == 1
    session = uuid.uuid4().hex
    payload = {"ItemId": item, "MediaSourceId": item, "PlaySessionId": session, "PositionTicks": 0,
               "IsPaused": True, "PlayMethod": "DirectPlay", "CanSeek": True}
    if playback:
        client.request("/Sessions/Playing", "POST", payload, 204)
    timings = []
    try:
        for index in range(55):
            query = urllib.parse.urlencode({"query": f"JellySin Load {(index * 191 + worker) % count:06d}"})
            started = time.perf_counter()
            if playback:
                client.request("/Sessions/Playing/Progress", "POST", {**payload, "EventName": "TimeUpdate"}, 204)
            else:
                result = client.request(PREFIX + "/Me/Library/Search?" + query)
                require(len(result) == 1, "Indexed plugin search did not return the exact generated track.")
            if index >= 5:
                timings.append((time.perf_counter() - started) * 1000)
    finally:
        if playback:
            client.request("/Sessions/Playing/Stopped", "POST", payload, 204)
    return ("playbackHttp" if playback else "pluginSearchHttp"), timings


def exercise(host: DockerHost, count: int) -> dict:
    fixtures(host.fixture_directory, count)
    host.start()
    admin = Client(host.origin)
    setup_server(admin, secrets.token_urlsafe(24))
    plugin_settings(admin, Enabled=True, MetadataEnabled=False, SimilarityEnabled=False)
    password = secrets.token_urlsafe(24)
    create_user(admin, password)
    listener = Client(host.origin)
    listener.authenticate("SmokeListener", password)
    print(f"Scanning {count} generated tracks with internet providers disabled.", flush=True)
    scan_seconds, item = scan(admin, count)
    with ThreadPoolExecutor(max_workers=8) as executor:
        pending = [executor.submit(request_workload, host.origin, listener.token, item, worker, count) for worker in range(8)]
        results = [future.result(timeout=180) for future in pending]
    metrics = {}
    for kind in ("playbackHttp", "pluginSearchHttp"):
        values = sorted(value for name, samples in results if name == kind for value in samples)
        metrics[kind] = {"samples": len(values), "p50Milliseconds": values[len(values) // 2],
                         "p95Milliseconds": values[int(len(values) * .95)], "p99Milliseconds": values[int(len(values) * .99)]}
        require(metrics[kind]["p95Milliseconds"] <= 1000, "The real-library HTTP p95 budget of 1000 ms was exceeded.")
    delivery = listener.request(PREFIX + "/Me/Delivery")
    require(delivery["pending"] == 0 and delivery["failedwrites"] == 0 and delivery["droppedsnapshots"] == 0,
            "The unconnected load-test account must have no queued scrobbles or failed observations.")
    return {"image": host.image, "libraryTracks": count, "scanSeconds": scan_seconds,
            "containerCpus": 2, "containerMemoryMiB": 1536, "parallelHttpWorkers": 8,
            "hostNetworkIncluded": True, "lastfmCalls": 0, "p95BudgetMilliseconds": 1000, "metrics": metrics}


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plugin", type=Path, required=True)
    parser.add_argument("--image", default=DEFAULT_IMAGE)
    parser.add_argument("--tracks", type=int, default=10000, help="Generated library size, from 1000 to 20000 tracks.")
    parser.add_argument("--report", type=Path, default=Path("build/library-load.json"))
    args = parser.parse_args()
    require(1000 <= args.tracks <= 20000, "Library size must be between 1000 and 20000 tracks.")
    require("@sha256:" in args.image, "The Jellyfin image must be pinned to a digest.")
    host = DockerHost(args.plugin.resolve(strict=True), args.image)
    try:
        report = exercise(host, args.tracks)
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        print("Real-library search and concurrent playback HTTP budgets passed.", flush=True)
        return 0
    finally:
        host.cleanup()


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (SmokeFailure, OSError, ValueError, KeyError, urllib.error.URLError) as error:
        raise SystemExit(str(error) if isinstance(error, SmokeFailure) else "Library load check failed: " + type(error).__name__) from None
