#!/usr/bin/env python3
"""Open the plugin's pages in a real browser on an isolated Jellyfin server, with a Last.fm stand-in.

The server runs from a pinned image (smoke.py) on a Docker network made for this run and reaches "Last.fm"
only through the stand-in (lastfm_stand_in.py), a proxy in its own container on that network. The proxy
accepts a tunnel to ws.audioscrobbler.com alone and ends TLS there with a certificate from a certificate
authority made for this run; the server trusts nothing else for it. No port opens on the workstation besides
the server's own loopback port. The browser's request for Last.fm's approval page is answered with an
approval for a fixed token. No real Last.fm account, application or credential is used.

The browser half (page_check.mjs) signs in as an administrator and saves the plugin settings and a made-up
application, connects an ordinary user, and opens charts, recent listens and one discovery result. The
evidence directory receives one screenshot per page, the server log, the methods the stand-in answered and
its tunnel record.
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
from lastfm_stand_in import ARTIST, LASTFM_USER, PORT, SEED, SIMILAR_LOCAL, SIMILAR_REMOTE, make_certificates  # noqa: E402
from smoke import (  # noqa: E402
    PREFIX, Client, DockerHost, SmokeFailure, add_library, add_server_options, chosen_image, docker, fixture_music,
    remove_container, require, setup_server, wait_music,
)

# Python alone, pinned by digest like the server image; the stand-in needs nothing beyond the standard library.
STAND_IN_IMAGE = "python:3.14.8-slim@sha256:a2b82f3c48559aa0a8446d9af49826b6e2b2016f4cd2afabfe6013ec53729170"
STAND_IN_ALIAS = "lastfm-stand-in"
LISTENER = "PageListener"
READY_SECONDS = 30


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
    fixture_music(host.fixture_directory / "Public", SIMILAR_LOCAL, name="similar")
    add_library(admin, "Public")
    admin.request("/Library/Refresh", "POST", expected=204)
    tracks = wait_music(admin)
    seed = next(item["id"] for item in tracks if item["name"] == SEED)
    listener = admin.request("/Users/New", "POST", {"Name": LISTENER, "Password": listener_password})
    require(not listener["policy"]["isadministrator"], "The ordinary user unexpectedly has administrator rights.")
    return {"direct": host.origin, "serverId": admin.request("/System/Info/Public")["id"],
            "admin": {"id": admin_user["id"], "token": admin.token},
            "listener": {"name": LISTENER, "password": listener_password}, "seedItemId": seed,
            "lastfmUser": LASTFM_USER, "artist": ARTIST, "seed": SEED, "similarLocal": SIMILAR_LOCAL, "similarRemote": SIMILAR_REMOTE}


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
            write_evidence(evidence, host, stand_in)
            # Jellyfin itself also asks the proxy for its own hosts (plugin catalogue, MusicBrainz); those refusals are
            # recorded, not failures. A Last.fm tunnel that carried no request means the server rejected the stand-in.
            require(not [line for line in stand_in.lines("tunnels.log") if line.startswith(("tls-failed ", "no-request "))],
                    "The server did not accept the stand-in's certificate for the Last.fm API.")
            require(any(line.endswith(" auth.getSession") for line in stand_in.lines("calls.log")), "The server never asked the stand-in for a session.")
            require(browser.returncode == 0, "A page check failed; see the lines above and the evidence directory.")
            print(f"PASS: Jellyfin {host.version}: administrator settings, connection, charts, recent listens and discovery.", flush=True)
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
