#!/usr/bin/env python3
"""Open the plugin's pages in a real browser on an isolated Jellyfin server, with a Last.fm stand-in.

The server runs from a pinned image (smoke.py) on a Docker network made for this run and reaches "Last.fm"
only through the stand-in (lastfm_stand_in.py), a proxy in its own container on that network. The proxy
accepts a tunnel to ws.audioscrobbler.com alone and ends TLS there with a certificate from a certificate
authority made for this run; the server trusts nothing else for it. No port opens on the workstation besides
the server's own loopback port. The browser's request for Last.fm's approval page is answered with an
approval for a fixed token. No real Last.fm account, application or credential is used.

The browser half (page_check.mjs) signs in as an administrator and saves the plugin settings and a made-up
application, connects an ordinary user, and opens charts, recent listens and one discovery result. Then that
user's own client plays a library track in real time past the scrobble threshold, imports the listening history
into the native play counts and turns on favourite synchronization; each flow must reach the stand-in with the
track it names and leave the native user data the API reports. The evidence directory receives one screenshot
per page, the server log, the methods the stand-in answered and its tunnel record.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
from typing import Any
import uuid

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
from lastfm_stand_in import (  # noqa: E402
    ARTIST, LASTFM_USER, PORT, SEED, SEED_PLAYS, SIMILAR_LOCAL, SIMILAR_PLAYS, SIMILAR_REMOTE, make_certificates,
)
from smoke import (  # noqa: E402
    PREFIX, Client, DockerHost, SmokeFailure, add_library, add_server_options, chosen_image, docker, fixture_music,
    remove_container, require, setup_server, wait_music,
)

# Python alone, pinned by digest like the server image; the stand-in needs nothing beyond the standard library.
STAND_IN_IMAGE = "python:3.14.8-slim@sha256:a2b82f3c48559aa0a8446d9af49826b6e2b2016f4cd2afabfe6013ec53729170"
STAND_IN_ALIAS = "lastfm-stand-in"
LISTENER = "PageListener"
READY_SECONDS = 30
TICKS_PER_SECOND = 10_000_000
# The 65-second fixture counts as heard after half of it; the player reports progress every few seconds until then.
PLAYED_SECONDS = 36
PROGRESS_SECONDS = 5
CALL_SECONDS = 60


class StandInContainer:
    """The run's network and the stand-in container on it; both are removed again by cleanup."""

    def __init__(self, directory: Path) -> None:
        self.directory = directory
        self.network = "jellysin-page-check-" + uuid.uuid4().hex[:16]
        self.name = self.network + "-stand-in"
        self.session = "stand-in-session-" + secrets.token_hex(8)
        self.network_created = False
        self.started = False

    def start(self) -> None:
        make_certificates(self.directory)
        shutil.copy2(Path(__file__).resolve().parent / "lastfm_stand_in.py", self.directory / "lastfm_stand_in.py")
        (self.directory / "session.txt").write_text(self.session + "\n", encoding="utf-8")
        (self.directory / "out").mkdir()
        try:
            docker("image", "inspect", STAND_IN_IMAGE)
        except SmokeFailure:
            print("Pulling the pinned stand-in image.", flush=True)
            docker("pull", STAND_IN_IMAGE, timeout=300)
        docker("network", "create", "--label", "org.jellysin.smoke=true", self.network)
        self.network_created = True
        docker("run", "--detach", "--name", self.name, "--label", "org.jellysin.smoke=true", "--network", self.network,
               "--network-alias", STAND_IN_ALIAS, "--user", f"{os.getuid()}:{os.getgid()}", "--memory", "256m", "--cpus", "1",
               "--read-only", "--mount", f"type=bind,src={self.directory},dst=/stand-in",
               STAND_IN_IMAGE, "python", "-I", "/stand-in/lastfm_stand_in.py", "/stand-in")
        self.started = True
        deadline = time.monotonic() + READY_SECONDS
        while not (self.directory / "out" / "ready").exists():
            require(time.monotonic() < deadline, "The Last.fm stand-in did not start listening.")
            time.sleep(0.2)

    def server_arguments(self) -> tuple[str, ...]:
        address = f"http://{STAND_IN_ALIAS}:{PORT}"
        return ("--network", self.network, "--env", "HTTPS_PROXY=" + address, "--env", "HTTP_PROXY=" + address,
                "--env", "NO_PROXY=localhost,127.0.0.1", "--env", "SSL_CERT_FILE=/stand-in/ca.pem",
                "--mount", f"type=bind,src={self.directory / 'ca.pem'},dst=/stand-in/ca.pem,readonly")

    def lines(self, name: str) -> list[str]:
        path = self.directory / "out" / name
        return path.read_text(encoding="utf-8").splitlines() if path.exists() else []

    def cleanup(self) -> None:
        failures = []
        if self.started and not remove_container(self.name):
            failures.append("container")
        if self.network_created:
            try:
                docker("network", "rm", self.network)
            except SmokeFailure:
                failures.append("network")
        require(not failures, "Some uniquely owned page check Docker resources could not be removed.")


def prepare_server(host: DockerHost) -> dict[str, Any]:
    password, listener_password = secrets.token_urlsafe(24), secrets.token_urlsafe(24)
    admin = Client(host.origin)
    admin_user = setup_server(admin, password)
    # Named after its title: Jellyfin lists a new track under its file name until the embedded title is read.
    fixture_music(host.fixture_directory / "Public", SIMILAR_LOCAL)
    add_library(admin, "Public")
    admin.request("/Library/Refresh", "POST", expected=204)
    tracks = wait_music(admin)
    seed = next(item["id"] for item in tracks if item["name"] == SEED)
    similar = next(item["id"] for item in tracks if item["name"] == SIMILAR_LOCAL)
    listener = admin.request("/Users/New", "POST", {"Name": LISTENER, "Password": listener_password})
    require(not listener["policy"]["isadministrator"], "The ordinary user unexpectedly has administrator rights.")
    return {"direct": host.origin, "serverId": admin.request("/System/Info/Public")["id"],
            "admin": {"id": admin_user["id"], "token": admin.token},
            "listener": {"name": LISTENER, "password": listener_password}, "seedItemId": seed, "similarItemId": similar,
            "lastfmUser": LASTFM_USER, "artist": ARTIST, "seed": SEED, "similarLocal": SIMILAR_LOCAL, "similarRemote": SIMILAR_REMOTE}


def named(title: str) -> str:
    return f" | {ARTIST} - {title}"


def wait_for_call(stand_in: StandInContainer, line: str) -> None:
    deadline = time.monotonic() + CALL_SECONDS
    while line not in stand_in.lines("calls.log"):
        require(time.monotonic() < deadline, f"The stand-in never received {line!r}.")
        time.sleep(0.5)


def user_data(listener: Client, item: str) -> dict[str, Any]:
    return listener.request("/UserItems/" + item + "/UserData")


def playback_flow(listener: Client, stand_in: StandInContainer, state: dict[str, Any]) -> str:
    """A player that plays the seed track in real time: its reported position never runs ahead of the clock."""
    item = state["seedItemId"]
    playing = {"ItemId": item, "PositionTicks": 0, "IsPaused": False, "PlayMethod": "DirectPlay", "CanSeek": True,
               "PlaySessionId": uuid.uuid4().hex, "MediaSourceId": item}
    started = time.monotonic()
    listener.request("/Sessions/Playing", "POST", playing, 204)
    position = 0
    while position < PLAYED_SECONDS:
        time.sleep(PROGRESS_SECONDS)
        position = int(time.monotonic() - started)
        listener.request("/Sessions/Playing/Progress", "POST",
                         {**playing, "PositionTicks": position * TICKS_PER_SECOND, "EventName": "TimeUpdate"}, 204)
    listener.request("/Sessions/Playing/Stopped", "POST", {**playing, "PositionTicks": position * TICKS_PER_SECOND}, 204)
    wait_for_call(stand_in, "POST track.updateNowPlaying" + named(SEED))
    wait_for_call(stand_in, "POST track.scrobble" + named(SEED))
    deadline = time.monotonic() + CALL_SECONDS
    while (delivery := listener.request(PREFIX + "/Me/Delivery"))["pending"] or delivery["pendingpersistence"]:
        require(time.monotonic() < deadline, f"The accepted scrobble stayed queued: {delivery}.")
        time.sleep(0.5)
    require(not (delivery["rejected"] or delivery["failedwrites"] or delivery["droppedsnapshots"] or delivery["legacyplugindetected"]),
            f"Delivery reports a problem after the scrobble: {delivery}.")
    return f"played {position} s; now playing and one scrobble reached the stand-in; delivery queue empty"


def history_flow(listener: Client, stand_in: StandInContainer, state: dict[str, Any]) -> str:
    """The import proposes Last.fm's overall counts as floors and writes them into the native user data."""
    seed, similar = state["seedItemId"], state["similarItemId"]
    floors = {seed: max(user_data(listener, seed)["playcount"], SEED_PLAYS), similar: max(user_data(listener, similar)["playcount"], SIMILAR_PLAYS)}
    preview = listener.request(PREFIX + "/Me/History/Preview", "POST")
    for _ in range(10):
        if preview["complete"]:
            break
        preview = listener.request(PREFIX + "/Me/History/Continue", "POST", {"PreviewId": preview["id"]})
    require(preview["complete"], "The history preview never completed.")
    proposed = {uuid.UUID(entry["itemid"]): entry["proposedplaycount"] for entry in preview["entries"]}
    require(proposed == {uuid.UUID(item): count for item, count in floors.items()}, f"The preview proposes {proposed}, not {floors}.")
    result = listener.request(PREFIX + "/Me/History/Import", "POST", {"PreviewId": preview["id"]})
    require(result["complete"] and result["applied"] == result["total"] == 2, f"The import answered {result}.")
    counts = {item: user_data(listener, item)["playcount"] for item in floors}
    require(counts == floors, f"The native play counts are {counts}, not {floors}.")
    require(user_data(listener, similar)["lastplayeddate"], "The import left the similar track without a last-played date.")
    return f"native play counts now {sorted(counts.values())}"


def favourite_flow(listener: Client, stand_in: StandInContainer, state: dict[str, Any]) -> str:
    """A library favourite is loved on Last.fm, and a track loved there only becomes a library favourite."""
    seed, similar = state["seedItemId"], state["similarItemId"]
    require(listener.request("/UserFavoriteItems/" + seed, "POST")["isfavorite"], "The native favourite was not saved.")
    require(not user_data(listener, similar)["isfavorite"], "The similar track is a favourite before the synchronization.")
    review = listener.request(PREFIX + "/Me/Favourites", "PUT", {"Enabled": True})
    deadline = time.monotonic() + CALL_SECONDS
    while review["running"] or not review["lastsync"]:
        require(time.monotonic() < deadline, f"The favourite synchronization did not finish: {review.get('status')}.")
        time.sleep(1)
        review = listener.request(PREFIX + "/Me/Favourites")
    wait_for_call(stand_in, "POST track.love" + named(SEED))
    require(user_data(listener, similar)["isfavorite"], "The track loved on Last.fm did not become a library favourite.")
    require(review["total"] == 0, f"The first synchronization proposed {review['total']} removals.")
    return f"seed loved on Last.fm, similar track marked in the library; status {review['status']!r}"


def listener_flows(origin: str, stand_in: StandInContainer, state: dict[str, Any]) -> bool:
    """Runs every flow as the connected ordinary user, prints one line each and reports whether all passed."""
    listener = Client(origin)
    listener.authenticate(state["listener"]["name"], state["listener"]["password"])
    passed = True
    for name, flow in (("playback reaches Last.fm", playback_flow), ("history import", history_flow), ("favourite synchronization", favourite_flow)):
        try:
            print(f"PASS: {name}: {flow(listener, stand_in, state)}", flush=True)
        except SmokeFailure as failure:
            print(f"FAIL: {name}: {failure}", flush=True)
            passed = False
    return passed


def write_evidence(evidence: Path, host: DockerHost, stand_in: StandInContainer) -> None:
    log = subprocess.run(["docker", "logs", host.name], capture_output=True, text=True, encoding="utf-8",
                         errors="replace", timeout=60, check=False)
    text = log.stdout + log.stderr
    require(stand_in.session not in text, "The server log contains the Last.fm session key.")
    (evidence / "server.log").write_text(text, encoding="utf-8")
    for name in ("calls.log", "tunnels.log"):
        (evidence / ("stand-in-" + name)).write_text("".join(line + "\n" for line in stand_in.lines(name)), encoding="utf-8")


def run(image: str, plugin: Path, evidence: Path) -> None:
    evidence.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="jellysin-page-check-") as name:
        directory = Path(name)
        (directory / "stand-in").mkdir(mode=0o755)
        stand_in = StandInContainer(directory / "stand-in")
        host = DockerHost(plugin, image, extra_arguments=stand_in.server_arguments())
        try:
            stand_in.start()
            host.start()
            state = prepare_server(host)
            state.update({"origin": host.origin, "evidence": str(evidence.resolve()), "version": host.version, "prefix": PREFIX})
            # The administrator's token and both passwords stay outside the directory the stand-in sees.
            run_file = directory / "run.json"
            run_file.write_text(json.dumps(state), encoding="utf-8")
            run_file.chmod(0o600)
            browser = subprocess.run(["node", str(ROOT / "tests/integration/page_check.mjs"), str(run_file)],
                                     cwd=ROOT, timeout=600, check=False)
            # The flows need the connection the browser made; without it they would only repeat its failure.
            flows = browser.returncode == 0 and listener_flows(host.origin, stand_in, state)
            write_evidence(evidence, host, stand_in)
            # Jellyfin itself also asks the proxy for its own hosts (plugin catalogue, MusicBrainz); those refusals are
            # recorded, not failures. A Last.fm tunnel that carried no request means the server rejected the stand-in.
            require(not [line for line in stand_in.lines("tunnels.log") if line.startswith(("tls-failed ", "no-request "))],
                    "The server did not accept the stand-in's certificate for the Last.fm API.")
            require(any(line.endswith(" auth.getSession") for line in stand_in.lines("calls.log")), "The server never asked the stand-in for a session.")
            require(browser.returncode == 0, "A page check failed; see the lines above and the evidence directory.")
            require(flows, "A listener flow failed; see the lines above and the evidence directory.")
            print(f"PASS: Jellyfin {host.version}: administrator settings, connection, charts, recent listens, discovery, "
                  "scrobbling, history import and favourite synchronization.", flush=True)
        finally:
            try:
                host.cleanup()
            finally:
                stand_in.cleanup()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plugin", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True, help="Directory for screenshots and logs; keep it out of the repository.")
    add_server_options(parser)
    args = parser.parse_args()
    try:
        run(chosen_image(parser, args), args.plugin.resolve(strict=True), args.evidence)
    except SmokeFailure as failure:
        print(f"FAIL: {failure}", file=sys.stderr, flush=True)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
