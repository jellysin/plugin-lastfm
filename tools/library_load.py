#!/usr/bin/env python3
"""Measure bounded plugin search and playback HTTP requests on a real synthetic music library, and scrobble delivery under that load.

While the search and playback clients run, connected accounts play to the scrobble threshold against the Last.fm
stand-in of tools/scrobble_delivery.py, through an open period, a period the stand-in refuses, and a period in which
one player reports only its start and stop. With --plant-dropped-listen the stand-in loses one listen, and the check
must fail.
"""

from __future__ import annotations

import argparse
from concurrent.futures import ThreadPoolExecutor
import json
from pathlib import Path
import secrets
import shutil
import subprocess
import tempfile
import threading
import struct
import time
import urllib.error
import urllib.parse
import uuid
import wave

import scrobble_delivery
from page_check import StandInContainer
from smoke import Client, DockerHost, PREFIX, SmokeFailure, add_library, add_server_options, chosen_image, create_user, plugin_settings, require, setup_server

P95_BUDGET_MILLISECONDS = 1000
# Each load client runs at least MIN_REQUESTS requests and keeps going until the delivery check ends, at most MAX_REQUESTS.
MIN_REQUESTS = 55
MAX_REQUESTS = 50_000
LOAD_SECONDS = 1500


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


def request_workload(origin: str, token: str, item: str, worker: int, count: int, stop: threading.Event | None = None) -> tuple[str, list[float]]:
    client = Client(origin, token=token)
    playback = worker % 2 == 1
    session = uuid.uuid4().hex
    payload = {"ItemId": item, "MediaSourceId": item, "PlaySessionId": session, "PositionTicks": 0,
               "IsPaused": True, "PlayMethod": "DirectPlay", "CanSeek": True}
    if playback:
        client.request("/Sessions/Playing", "POST", payload, 204)
    timings = []
    try:
        for index in range(MAX_REQUESTS):
            if index >= MIN_REQUESTS and (stop is None or stop.is_set()):
                break
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


def summarize(results: list[tuple[str, list[float]]]) -> dict:
    """Percentiles per request kind; refuse a kind without samples or one whose p95 exceeds the budget."""
    metrics = {}
    for kind in ("playbackHttp", "pluginSearchHttp"):
        values = sorted(value for name, samples in results if name == kind for value in samples)
        require(bool(values), f"The load run measured no {kind} requests.")
        metrics[kind] = {"samples": len(values), "p50Milliseconds": values[len(values) // 2],
                         "p95Milliseconds": values[int(len(values) * .95)], "p99Milliseconds": values[int(len(values) * .99)]}
        require(metrics[kind]["p95Milliseconds"] <= P95_BUDGET_MILLISECONDS,
                f"The real-library HTTP p95 budget of {P95_BUDGET_MILLISECONDS} ms was exceeded.")
    return metrics


def loaded(executor: ThreadPoolExecutor, host: DockerHost, token: str, item: str, count: int, during) -> tuple[list, dict]:
    """Runs the eight load clients until during() returns, and at least MIN_REQUESTS requests each."""
    stop = threading.Event()
    pending = [executor.submit(request_workload, host.origin, token, item, worker, count, stop) for worker in range(8)]
    try:
        outcome = during()
    finally:
        stop.set()
    return [future.result(timeout=LOAD_SECONDS) for future in pending], outcome


def exercise(host: DockerHost, stand_in: StandInContainer, count: int) -> dict:
    fixtures(host.fixture_directory, count)
    scrobble_delivery.fixtures(host.fixture_directory)
    host.start()
    admin = Client(host.origin)
    setup_server(admin, secrets.token_urlsafe(24))
    plugin_settings(admin, Enabled=True)
    admin.request(PREFIX + "/Admin/Application", "PUT", {"ApiKey": "a" * 32, "Secret": "b" * 32}, expected=(200, 204))
    password = secrets.token_urlsafe(24)
    create_user(admin, password)
    listener = Client(host.origin)
    listener.authenticate("SmokeListener", password)
    print(f"Scanning {count} generated tracks with internet providers disabled.", flush=True)
    scan_seconds, item = scan(admin, count + len(scrobble_delivery.TITLES))
    ids = scrobble_delivery.items(admin)
    accounts = scrobble_delivery.connect(admin, password)
    with ThreadPoolExecutor(max_workers=8 + len(accounts)) as executor:
        results, delivery = loaded(executor, host, listener.token, item, count,
                                   lambda: scrobble_delivery.deliver(executor, stand_in, accounts, ids))
    metrics = summarize(results)
    unconnected = listener.request(PREFIX + "/Me/Delivery")
    require(unconnected["pending"] == 0 and unconnected["failedwrites"] == 0 and unconnected["droppedsnapshots"] == 0,
            "The unconnected load-test account must have no queued scrobbles or failed observations.")
    return {"image": host.image, "server": host.version, "libraryTracks": count, "scanSeconds": scan_seconds,
            "containerCpus": 2, "containerMemoryMiB": 1536, "parallelHttpWorkers": 8,
            "hostNetworkIncluded": True, "lastfmCalls": delivery.pop("lastfmCalls"), "delivery": delivery,
            "p95BudgetMilliseconds": P95_BUDGET_MILLISECONDS, "metrics": metrics}


def keep(host: DockerHost | None, stand_in: StandInContainer, report: Path) -> None:
    """Keeps the stand-in's call log and the server log of a failed run beside the report, so the failure can be read."""
    report.parent.mkdir(parents=True, exist_ok=True)
    calls = stand_in.directory / "out" / "calls.log"
    if calls.exists():
        shutil.copy2(calls, report.with_suffix(".calls.log"))
    if host and host.started:
        logs = subprocess.run(["docker", "logs", host.name], capture_output=True, text=True, timeout=60, check=False)
        report.with_suffix(".server.log").write_text(logs.stdout + logs.stderr, encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plugin", type=Path, required=True)
    add_server_options(parser)
    parser.add_argument("--tracks", type=int, default=10000, help="Generated library size, from 1000 to 20000 tracks.")
    parser.add_argument("--report", type=Path, default=Path("build/library-load.json"))
    parser.add_argument("--plant-dropped-listen", action="store_true", help="Make the stand-in lose one listen; the check must then fail.")
    args = parser.parse_args()
    require(1000 <= args.tracks <= 20000, "Library size must be between 1000 and 20000 tracks.")
    with tempfile.TemporaryDirectory(prefix="jellysin-library-load-") as directory:
        stand_in = StandInContainer(Path(directory))
        if args.plant_dropped_listen:
            (Path(directory) / "drop-listen.txt").write_text("drop\n", encoding="utf-8")
        host = None
        try:
            stand_in.start()
            host = DockerHost(args.plugin.resolve(strict=True), chosen_image(parser, args), extra_arguments=stand_in.server_arguments())
            report = exercise(host, stand_in, args.tracks)
            args.report.parent.mkdir(parents=True, exist_ok=True)
            args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
            print("Real-library search and concurrent playback HTTP budgets and scrobble delivery passed.", flush=True)
            return 0
        except SmokeFailure:
            keep(host, stand_in, args.report)
            raise
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
        raise SystemExit(str(error) if isinstance(error, SmokeFailure) else "Library load check failed: " + type(error).__name__) from None
