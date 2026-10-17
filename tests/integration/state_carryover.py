#!/usr/bin/env python3
"""Carry a connected account across a Jellyfin upgrade and a restore of the server's configuration.

The server reaches "Last.fm" only through the stand-in of the page check (lastfm_stand_in.py), with a made-up
application; no real Last.fm account, application or credential is used. One ordinary user connects on the
oldest supported Jellyfin 12 release and has a library favourite loved on Last.fm. Then:

- upgrade: the newest Jellyfin 12 release starts on the same configuration volume. The user is still connected
  and a second favourite is loved, which needs the stored session key read back with the stored key ring.
- restore: the configuration is copied into a fresh volume, as a backup restored on a new server would be, and
  the newest release starts on it with a fresh cache. The user is still connected and a third favourite is loved.
- restore without the key ring: the same copy without jellysin-lastfm/keys. The server starts, the administrator
  page reports the stored application override as unreadable, the account is kept but asks for a new connection, as
  docs/operations.md says, and no favourite reaches the stand-in.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import secrets
import sys
import tempfile
import time
from typing import Any
import urllib.error
import urllib.parse
import urllib.request

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
from lastfm_stand_in import APPROVAL_TOKEN, LASTFM_USER  # noqa: E402
from page_check import CALL_SECONDS, StandInContainer, named, wait_for_call  # noqa: E402
from smoke import (  # noqa: E402
    IMAGES, PREFIX, Client, DockerHost, SmokeFailure, add_library, create_user, docker, fixture_music, lower_keys,
    remove_container, require, setup_server, wait_music,
)

# One library favourite per server the account is carried to; each must reach the stand-in as its own love.
FAVOURITES = {"before": "Carryover Before", "upgrade": "Carryover Upgrade", "restore": "Carryover Restore",
              "keyless": "Carryover Without Keys"}
COOKIE = "JellySinLastfmAttempt"
# The favourites and the seed track every server writes into the Public library.
LIBRARY_TRACKS = len(FAVOURITES) + 1


def add_fixtures(host: DockerHost) -> None:
    for title in FAVOURITES.values():
        fixture_music(host.fixture_directory / "Public", title)


def approve(listener: Client, token: str = APPROVAL_TOKEN, user: str = LASTFM_USER) -> None:
    """Begin, return from the stand-in's approval with the attempt cookie, and finish, as the browser would.

    The stand-in signs token in as user; a token with a label of its own names a Last.fm user of its own."""
    payload, headers, _ = listener.request(PREFIX + "/Me/Connection/Begin", "POST", {"ReturnToSettings": False}, raw=True)
    cookie = next((part for part in headers.get("set-cookie", "").split(";") if part.strip().startswith(COOKIE + "=")), "")
    require(cookie, "Beginning the connection set no attempt cookie.")
    start = lower_keys(json.loads(payload))
    callback = urllib.parse.urlsplit(dict(urllib.parse.parse_qsl(urllib.parse.urlsplit(start["authorizationurl"]).query))["cb"])
    require(callback.path.endswith("/JellySin/Lastfm/Callback"), "The approval address does not return to this server.")
    query = urllib.parse.urlencode([*urllib.parse.parse_qsl(callback.query), ("token", token)])

    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *_: Any) -> None:
            return None

    request = urllib.request.Request(listener.origin + callback.path + "?" + query, headers={"Cookie": cookie.strip()})
    try:
        status = urllib.request.build_opener(NoRedirect).open(request, timeout=20).status
    except urllib.error.HTTPError as error:
        status = error.code
    require(status == 302, f"The return from the approval answered HTTP {status}.")
    status = listener.request(PREFIX + "/Me/Connection/Finish", "POST")
    require(status["connected"] and status["username"] == user and not status["needsreconnect"],
            f"Finishing the connection answered {status}.")


def connection(listener: Client) -> dict[str, Any]:
    return listener.request(PREFIX + "/Me")["connection"]


def love(listener: Client, stand_in: StandInContainer, title: str, first: bool, tracks: int = LIBRARY_TRACKS) -> None:
    """Marks a library favourite and synchronizes; the stand-in must receive the signed love for that track."""
    item = next(entry["id"] for entry in wait_music(listener, tracks) if entry["name"] == title)
    require(listener.request("/UserFavoriteItems/" + item, "POST")["isfavorite"], "The native favourite was not saved.")
    review = listener.request(PREFIX + "/Me/Favourites", "PUT", {"Enabled": True}) if first else \
        listener.request(PREFIX + "/Me/Favourites/Sync", "POST")
    deadline = time.monotonic() + CALL_SECONDS
    while review["running"]:
        require(time.monotonic() < deadline, f"The favourite synchronization did not finish: {review.get('status')}.")
        time.sleep(1)
        review = listener.request(PREFIX + "/Me/Favourites")
    wait_for_call(stand_in, "POST track.love" + named(title))


class Servers:
    """The stand-in and every server of the run; each volume is removed once, by whoever holds it last."""

    def __init__(self, plugin: Path, directory: Path) -> None:
        self.plugin = plugin
        self.stand_in = StandInContainer(directory)
        self.hosts: list[DockerHost] = []
        self.volumes: list[str] = []

    def start(self, image: str, config: str | None = None, cache: str | None = None) -> DockerHost:
        """A server on a fresh configuration volume, or on an existing one when config names it."""
        host = DockerHost(self.plugin, image, extra_arguments=self.stand_in.server_arguments())
        host.config, host.cache = config or host.config, cache or host.cache
        add_fixtures(host)
        self.hosts.append(host)
        host.start()
        self.volumes.extend(name for name in host.volumes if name not in self.volumes)
        host.volumes = []
        return host

    def stop(self, host: DockerHost) -> None:
        docker("stop", host.name, timeout=120)
        require(remove_container(host.name), "A stopped server could not be removed.")
        host.started = False

    def copy_configuration(self, image: str, source: str, keep_keys: bool) -> str:
        """Copies a stopped server's configuration into a new volume, with or without the plugin's key ring."""
        target = "jellysin-restore-" + secrets.token_hex(8)
        docker("volume", "create", "--label", "org.jellysin.smoke=true", target)
        self.volumes.append(target)
        # The key ring sits under Jellyfin's data directory; exactly one must exist before the copy can leave it out.
        script = ("cp -a /from/. /to/ && set -- $(find /to -type d -path '*/jellysin-lastfm/keys') && [ $# -eq 1 ]"
                  + ("" if keep_keys else ' && rm -r "$1"'))
        docker("run", "--rm", "--network", "none", "--user", "0:0", "--entrypoint", "/bin/sh",
               "--mount", f"type=volume,src={source},dst=/from,readonly", "--mount", f"type=volume,src={target},dst=/to",
               image, "-c", script)
        return target

    def cleanup(self) -> list[str]:
        failures = []
        for host in reversed(self.hosts):
            try:
                host.cleanup()
            except SmokeFailure:
                failures.append("server " + host.name)
        for volume in self.volumes:
            try:
                docker("volume", "rm", volume)
            except SmokeFailure:
                failures.append("volume " + volume)
        try:
            self.stand_in.cleanup()
        except SmokeFailure:
            failures.append("stand-in")
        return failures


def signed_in(host: DockerHost, password: str, name: str = "SmokeListener") -> Client:
    client = Client(host.origin)
    client.authenticate(name, password)
    return client


def require_connected(listener: Client, after: str) -> None:
    status = connection(listener)
    require(status["connected"] and status["username"] == LASTFM_USER and not status["needsreconnect"],
            f"After {after} the account reads {status}.")


def connect_on_floor(servers: Servers, image: str, password: str) -> DockerHost:
    host = servers.start(image)
    admin = Client(host.origin)
    setup_server(admin, password)
    add_library(admin, "Public")
    admin.request("/Library/Refresh", "POST", expected=204)
    admin.request(PREFIX + "/Admin/Application", "PUT", {"ApiKey": "a" * 32, "Secret": "b" * 32}, expected=(200, 204))
    create_user(admin, password)
    listener = signed_in(host, password)
    approve(listener)
    love(listener, servers.stand_in, FAVOURITES["before"], first=True)
    print(f"PASS: Jellyfin {host.version}: connected as {LASTFM_USER}; a favourite reached the stand-in.", flush=True)
    return host


def upgrade(servers: Servers, before: DockerHost, image: str, password: str) -> DockerHost:
    servers.stop(before)
    host = servers.start(image, before.config, before.cache)
    listener = signed_in(host, password)
    require_connected(listener, "the upgrade")
    love(listener, servers.stand_in, FAVOURITES["upgrade"], first=False)
    print(f"PASS: upgrade {before.version} -> {host.version}: still connected; a favourite reached the stand-in.", flush=True)
    return host


def restore(servers: Servers, source: DockerHost, image: str, password: str, keep_keys: bool) -> None:
    calls_before = len(servers.stand_in.lines("calls.log"))
    host = servers.start(image, servers.copy_configuration(image, source.config, keep_keys))
    listener = signed_in(host, password)
    title = FAVOURITES["restore" if keep_keys else "keyless"]
    if keep_keys:
        require_connected(listener, "the restore")
        love(listener, servers.stand_in, title, first=False)
        print(f"PASS: restore on Jellyfin {host.version}: still connected; a favourite reached the stand-in.", flush=True)
    else:
        application = lower_keys(signed_in(host, password, "SmokeAdmin").request(PREFIX + "/Admin/Application"))
        require(application == {"configured": False, "source": "Unreadable"},
                f"After a restore without the key ring the administrator page reads {application}.")
        status = connection(listener)
        require(status["connected"] and status["needsreconnect"],
                f"After a restore without the key ring the account reads {status}, not a request to connect again.")
        item = next(entry["id"] for entry in wait_music(listener, LIBRARY_TRACKS) if entry["name"] == title)
        listener.request("/UserFavoriteItems/" + item, "POST")
        # Without a readable application the client refuses before sending (Last.fm error 10), which the API answers with 502.
        listener.request(PREFIX + "/Me/Favourites/Sync", "POST", expected=502)
        time.sleep(5)
        reached = servers.stand_in.lines("calls.log")[calls_before:]
        require(not reached, f"The server without its key ring still reached the stand-in: {reached}.")
        print(f"PASS: restore without the key ring on Jellyfin {host.version}: the override reads as unreadable, the account "
              "asks to connect again, and a synchronization was refused without a call to the stand-in.", flush=True)
    servers.stop(host)


def run(plugin: Path, floor: str, newest: str) -> None:
    password = secrets.token_urlsafe(24)
    with tempfile.TemporaryDirectory(prefix="jellysin-carryover-") as name:
        (Path(name) / "stand-in").mkdir(mode=0o755)
        servers = Servers(plugin, Path(name) / "stand-in")
        try:
            servers.stand_in.start()
            upgraded = upgrade(servers, connect_on_floor(servers, floor, password), newest, password)
            servers.stop(upgraded)
            restore(servers, upgraded, newest, password, keep_keys=True)
            restore(servers, upgraded, newest, password, keep_keys=False)
            require(servers.stand_in.session not in "".join(servers.stand_in.lines("calls.log")),
                    "The stand-in record holds the session key.")
        except BaseException:
            for failure in servers.cleanup():
                print(f"Not removed: {failure}", file=sys.stderr, flush=True)
            raise
        failures = servers.cleanup()
        require(not failures, f"Some carry-over resources could not be removed: {failures}.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plugin", type=Path, required=True)
    args = parser.parse_args()
    try:
        run(args.plugin.resolve(strict=True), IMAGES["floor"], IMAGES["newest"])
    except SmokeFailure as failure:
        print(f"FAIL: {failure}", file=sys.stderr, flush=True)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
