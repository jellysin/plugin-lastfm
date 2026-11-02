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
import hashlib
import io
import json
import secrets
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
import zipfile
from collections.abc import Sequence
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
from lastfm_stand_in import APPROVAL_TOKEN, LASTFM_USER
from page_check import CALL_SECONDS, StandInContainer, named, wait_for_call
from smoke import (
    IMAGES,
    PLUGIN_ID,
    PREFIX,
    Client,
    DockerHost,
    SmokeFailure,
    add_library,
    create_user,
    docker,
    fixture_music,
    lower_keys,
    remove_container,
    require,
    setup_server,
    wait_music,
)

# One library favourite per server the account is carried to; each must reach the stand-in as its own love.
FAVOURITES = {"before": "Carryover Before", "upgrade": "Carryover Upgrade", "restore": "Carryover Restore",
              "keyless": "Carryover Without Keys"}
COOKIE = "JellySinLastfmAttempt"
# The favourites and the seed track every server writes into the Public library.
LIBRARY_TRACKS = len(FAVOURITES) + 1

PREVIOUS_RELEASE_VERSION = "0.5.0"
PREVIOUS_RELEASE_URL = (
    f"https://github.com/jellysin/plugin-lastfm/releases/download/v{PREVIOUS_RELEASE_VERSION}/"
    f"plugin-lastfm-{PREVIOUS_RELEASE_VERSION}.zip"
)
PREVIOUS_RELEASE_SHA256 = "d96498fadd577011aef33785b9e2be094cfbf9c8c2ca045de7ab4a47cfb78cc2"
PREVIOUS_RELEASE_MAX_BYTES = 4 * 1024 * 1024
PLUGIN_DLL = "JellySin.Plugin.Lastfm.dll"


class HttpsOnlyRedirects(urllib.request.HTTPRedirectHandler):
    """Follows a redirect only to another HTTPS address; the release host redirects to its storage host."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        """Refuses the redirect unless its target address uses HTTPS."""
        require(urllib.parse.urlsplit(newurl).scheme == "https", f"Release download redirected off HTTPS: {newurl}")
        return super().redirect_request(req, fp, code, msg, headers, newurl)


def download_release(
    url: str = PREVIOUS_RELEASE_URL,
    max_bytes: int = PREVIOUS_RELEASE_MAX_BYTES,
    timeout: float = 60.0,
) -> bytes:
    """Downloads the previous release archive over HTTPS only, reading at most max_bytes."""
    parsed = urllib.parse.urlsplit(url)
    require(parsed.scheme == "https", f"Release download URL must use HTTPS: {url}")
    request = urllib.request.Request(url, headers={"User-Agent": "JellySin-StateCarryover"})
    try:
        with urllib.request.build_opener(HttpsOnlyRedirects).open(request, timeout=timeout) as response:
            require(urllib.parse.urlsplit(response.geturl()).scheme == "https",
                    f"Release download ended off HTTPS: {response.geturl()}")
            archive = response.read(max_bytes + 1)
    except (urllib.error.URLError, OSError) as error:
        raise SmokeFailure(f"Failed to download previous release from {url}: {error}") from None
    require(len(archive) <= max_bytes, f"Downloaded release archive exceeded bound of {max_bytes} bytes.")
    return archive


def extract_assembly(
    archive: bytes,
    destination: Path,
    expected_sha256: str = PREVIOUS_RELEASE_SHA256,
    member_name: str = PLUGIN_DLL,
    max_bytes: int = PREVIOUS_RELEASE_MAX_BYTES,
) -> Path:
    """Checks the archive against its pinned SHA-256 and writes its single plugin assembly to destination."""
    require(len(archive) <= max_bytes, f"The release archive has {len(archive)} bytes, exceeding the bound of {max_bytes}.")
    actual_sha = hashlib.sha256(archive).hexdigest()
    require(actual_sha == expected_sha256, f"Release archive SHA-256 {actual_sha} does not match expected {expected_sha256}.")
    try:
        with zipfile.ZipFile(io.BytesIO(archive)) as bundle:
            members = [item for item in bundle.infolist() if Path(item.filename).name == member_name]
            require(len(members) == 1, f"The release archive holds no single {member_name} (found {len(members)}).")
            require(members[0].file_size <= max_bytes, f"Member {member_name} size {members[0].file_size} exceeds {max_bytes}.")
            data = bundle.read(members[0])
    except zipfile.BadZipFile as error:
        raise SmokeFailure(f"Release archive is not a valid zip file: {error}") from None
    target = destination / member_name
    target.write_bytes(data)
    return target


def add_fixtures(host: DockerHost) -> None:
    """Writes one fixture track per favourite into the server's public library folder."""
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
    """The Last.fm connection state the plugin reports for the signed-in account."""
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
    try:
        wait_for_call(stand_in, "POST track.love" + named(title))
    except SmokeFailure as error:
        # The synchronization's own status and the stand-in's last calls say why the love never left the server.
        listed = [(entry["name"], entry["id"], entry.get("artists")) for entry in audio_items(listener)]
        raise SmokeFailure(f"{error} Synchronization status: {review.get('status')!r}; last calls: "
                           f"{stand_in.lines('calls.log')[-5:]}; audio items: {listed}.") from error


def audio_items(client: Client) -> list[dict[str, Any]]:
    """The audio items the server lists, with their names and artists."""
    return client.request("/Items?recursive=true&includeItemTypes=Audio&limit=50")["items"]


def scan_task(admin: Client) -> dict[str, Any]:
    """The server's single library scan task."""
    tasks = [task for task in admin.request("/ScheduledTasks") if task["key"] == "RefreshLibrary"]
    require(len(tasks) == 1, "The server lists no single library scan task.")
    return tasks[0]


def wait_settled(admin: Client, tracks: int = LIBRARY_TRACKS) -> None:
    """Scans the library and waits until that scan ended and each fixture title names exactly one item with an artist.

    Every server start writes fresh fixture files under the same path, so the server rescans them at a moment of its own
    choosing; until a scan has finished, a title can briefly name two items, which the plugin's matcher leaves unresolved.
    """
    before = (scan_task(admin).get("lastexecutionresult") or {}).get("endtimeutc")
    admin.request("/Library/Refresh", "POST", expected=204)
    items: list[dict[str, Any]] = []
    for _ in range(240):
        task = scan_task(admin)
        ended = (task.get("lastexecutionresult") or {}).get("endtimeutc")
        if task["state"] == "Idle" and ended != before:
            items = audio_items(admin)
            names = [item["name"] for item in items]
            if len(items) == tracks and len(set(names)) == tracks and all(item.get("artists") for item in items):
                return
        time.sleep(0.5)
    raise SmokeFailure(f"The library did not settle on {tracks} distinct tracks with artists within 120 seconds: "
                       f"{[(item['name'], item.get('artists')) for item in items]}.")


def plugin_version(client: Client) -> str:
    """The plugin version Jellyfin lists; fails when the plugin is not loaded."""
    plugins = client.request("/Plugins")
    match = next((item for item in plugins if item["id"].replace("-", "") == PLUGIN_ID.replace("-", "")), None)
    require(match is not None, "The plugin is not listed in Jellyfin.")
    return str(match["version"])


class Servers:
    """The stand-in and every server of the run; each volume is removed once, by whoever holds it last."""

    def __init__(self, plugin: Path, directory: Path) -> None:
        self.plugin = plugin
        self.stand_in = StandInContainer(directory)
        self.hosts: list[DockerHost] = []
        self.volumes: list[str] = []

    def start(self, image: str, config: str | None = None, cache: str | None = None, plugin: Path | None = None) -> DockerHost:
        """A server on a fresh configuration volume, or on an existing one when config names it."""
        host = DockerHost(plugin or self.plugin, image, extra_arguments=self.stand_in.server_arguments())
        host.config, host.cache = config or host.config, cache or host.cache
        add_fixtures(host)
        self.hosts.append(host)
        host.start()
        self.volumes.extend(name for name in host.volumes if name not in self.volumes)
        host.volumes = []
        return host

    def stop(self, host: DockerHost) -> None:
        """Stops and removes a server container and keeps its volumes for the next start."""
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
        """Removes every server, volume and the stand-in; returns the resources that could not be removed."""
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
    """A client signed in to host as the named account."""
    client = Client(host.origin)
    client.authenticate(name, password)
    return client


def require_connected(listener: Client, after: str) -> None:
    """Fails unless the account is still connected to the stand-in user without a reconnect request."""
    status = connection(listener)
    require(status["connected"] and status["username"] == LASTFM_USER and not status["needsreconnect"],
            f"After {after} the account reads {status}.")


def connect_on_floor(servers: Servers, image: str, password: str, plugin: Path | None = None) -> DockerHost:
    """Sets up the floor server, connects the account and proves a first favourite reaches the stand-in."""
    host = servers.start(image, plugin=plugin)
    admin = Client(host.origin)
    setup_server(admin, password)
    host.plugin_version = plugin_version(admin)
    add_library(admin, "Public")
    admin.request("/Library/Refresh", "POST", expected=204)
    admin.request(PREFIX + "/Admin/Application", "PUT", {"ApiKey": "a" * 32, "Secret": "b" * 32}, expected=(200, 204))
    create_user(admin, password)
    listener = signed_in(host, password)
    approve(listener)
    love(listener, servers.stand_in, FAVOURITES["before"], first=True)
    print(f"PASS: Jellyfin {host.version} (plugin {host.plugin_version}): connected as {LASTFM_USER}; "
          "a favourite reached the stand-in.", flush=True)
    return host


def upgrade(servers: Servers, before: DockerHost, image: str, password: str, plugin: Path | None = None) -> DockerHost:
    """Restarts the floor configuration on the newest image and plugin; the connection and a new favourite must carry over."""
    servers.stop(before)
    host = servers.start(image, before.config, before.cache, plugin=plugin)
    admin = signed_in(host, password, "SmokeAdmin")
    host.plugin_version = plugin_version(admin)
    wait_settled(admin)
    listener = signed_in(host, password)
    require_connected(listener, "the upgrade")
    love(listener, servers.stand_in, FAVOURITES["upgrade"], first=False)
    print(f"PASS: upgrade Jellyfin {before.version} -> {host.version} "
          f"(plugin {before.plugin_version} -> {host.plugin_version}): still connected; a favourite reached the stand-in.",
          flush=True)
    return host


def restore(servers: Servers, source: DockerHost, image: str, password: str, keep_keys: bool,
            plant: str | None = None) -> None:
    """Starts a copy of the configuration; with the key ring it must stay connected, without it it must refuse to call Last.fm."""
    calls_before = len(servers.stand_in.lines("calls.log"))
    drop = (not keep_keys) or (plant == "drop-keys")
    host = servers.start(image, servers.copy_configuration(image, source.config, keep_keys=not drop))
    wait_settled(signed_in(host, password, "SmokeAdmin"))
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
        # Without a readable application the synchronization is refused before any call, with the problem type of a
        # missing application and HTTP 503 (Api/ApiExceptionFilter.cs).
        problem = lower_keys(listener.request(PREFIX + "/Me/Favourites/Sync", "POST", expected=503))
        require(problem.get("type") == "urn:jellysin:lastfm:application-missing",
                f"A synchronization without the key ring answered {problem}, not a missing application.")
        time.sleep(5)
        reached = servers.stand_in.lines("calls.log")[calls_before:]
        require(not reached, f"The server without its key ring still reached the stand-in: {reached}.")
        print(f"PASS: restore without the key ring on Jellyfin {host.version}: the override reads as unreadable, the account "
              "asks to connect again, and a synchronization was refused without a call to the stand-in.", flush=True)
    servers.stop(host)


def run(
    plugin: Path,
    floor: str,
    newest: str,
    previous_release: bool = False,
    plant: str | None = None,
    report: dict[str, Any] | None = None,
) -> None:
    """Runs connect, upgrade, restore and keyless restore, records each step in report and removes every resource."""
    password = secrets.token_urlsafe(24)
    with tempfile.TemporaryDirectory(prefix="jellysin-carryover-") as name:
        stand_in_dir = Path(name) / "stand-in"
        stand_in_dir.mkdir(mode=0o755)
        servers = Servers(plugin, stand_in_dir)
        floor_plugin = plugin
        if previous_release:
            release_dir = Path(name) / "previous-release"
            release_dir.mkdir(mode=0o755)
            archive = download_release()
            floor_plugin = extract_assembly(archive, release_dir)
        assemblies = {"floor": hashlib.sha256(floor_plugin.read_bytes()).hexdigest(),
                      "upgraded": hashlib.sha256(plugin.read_bytes()).hexdigest()}
        if report is not None:
            report["assemblies"] = assemblies
        # Both releases can carry the same version number, so the assembly hash shows the upgrade swapped the plugin.
        require(not previous_release or assemblies["floor"] != assemblies["upgraded"],
                "The build under test is the previous release itself; the upgrade would change nothing.")
        try:
            servers.stand_in.start()
            connected = connect_on_floor(servers, floor, password, plugin=floor_plugin)
            if report is not None:
                report.setdefault("steps", []).append("connect")
            upgraded = upgrade(servers, connected, newest, password, plugin=plugin)
            if report is not None:
                report.setdefault("steps", []).append("upgrade")
                report["versions"] = {
                    "server": {"floor": connected.version, "upgraded": upgraded.version},
                    "plugin": {"floor": connected.plugin_version, "upgraded": upgraded.plugin_version},
                }
            servers.stop(upgraded)
            restore(servers, upgraded, newest, password, keep_keys=True, plant=plant)
            if report is not None:
                report.setdefault("steps", []).append("restore")
            restore(servers, upgraded, newest, password, keep_keys=False)
            if report is not None:
                report.setdefault("steps", []).append("keyless")
            require(servers.stand_in.session not in "".join(servers.stand_in.lines("calls.log")),
                    "The stand-in record holds the session key.")
        except BaseException:
            for failure in servers.cleanup():
                print(f"Not removed: {failure}", file=sys.stderr, flush=True)
            raise
        failures = servers.cleanup()
        require(not failures, f"Some carry-over resources could not be removed: {failures}.")
        # Passed only once the cleanup is known to be complete.
        if report is not None:
            report["passed"] = True


def build_parser() -> argparse.ArgumentParser:
    """The command-line options of the carry-over check."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plugin", type=Path, required=True, help="The current plugin assembly build.")
    parser.add_argument("--previous-release", action="store_true",
                        help="Download and test upgrade from the previous published release.")
    parser.add_argument("--plant", choices=("drop-keys",), help="Plant a defect the check must refuse.")
    parser.add_argument("--report", type=Path, help="Where to write the JSON record of the run.")
    return parser


def main(argv: Sequence[str] | None = None) -> int:
    """Runs the check, writes the report and returns 0 only for a passed, failure-free run."""
    parser = build_parser()
    args = parser.parse_args(argv)
    floor, newest = IMAGES["floor"], IMAGES["newest"]
    report: dict[str, Any] = {
        "plant": args.plant,
        "previous_release": args.previous_release,
        "images": {"floor": floor, "newest": newest},
        "steps": [],
    }
    try:
        run(
            args.plugin.resolve(strict=True),
            floor,
            newest,
            previous_release=args.previous_release,
            plant=args.plant,
            report=report,
        )
        print("PASS: state carry-over verified across upgrade and restore.", flush=True)
    except SmokeFailure as failure:
        report["failure"] = str(failure)
        print(f"FAIL: {failure}", file=sys.stderr, flush=True)
    finally:
        if args.report:
            args.report.parent.mkdir(parents=True, exist_ok=True)
            args.report.write_text(json.dumps(report, indent=1) + "\n", encoding="utf-8")
    # A recorded failure wins over a pass flag, so a report never claims both.
    return 0 if report.get("passed") and "failure" not in report else 1


if __name__ == "__main__":
    raise SystemExit(main())
