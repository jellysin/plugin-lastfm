#!/usr/bin/env python3
"""Exercise an isolated, real Jellyfin 12 server without contacting Last.fm.

The default run removes only its uniquely named Docker container and volumes.
--keep retains the host after success; --state-file writes its access details with
owner-only permissions for a local browser session. Never publish that file.
"""

from __future__ import annotations

import argparse
import contextlib
import http.client
import http.server
import json
import os
from pathlib import Path
import re
import secrets
import struct
import subprocess
import tempfile
import threading
import time
from typing import Any
import urllib.error
import urllib.parse
import urllib.request
import uuid
import wave


PLUGIN_ID = "2034650d-a290-4a16-b195-89fb44cfb932"
DEFAULT_IMAGE = "jellyfin/jellyfin:12.0@sha256:baba630419915985442f315f08b0cf46d9f4c8a0cc4bd38e94a6d35751dd5ef5"
PREFIX = "/JellySin/Lastfm"
MAX_RESPONSE = 8_000_000


class SmokeFailure(RuntimeError):
    """An assertion failure containing no server response or credentials."""


def require(condition: Any, description: str) -> None:
    if not condition:
        raise SmokeFailure(description)


def docker(*arguments: str, timeout: int = 90) -> str:
    command = subprocess.run(
        ["docker", *arguments], capture_output=True, text=True,
        encoding="utf-8", errors="replace", timeout=timeout, check=False,
    )
    if command.returncode:
        # Docker arguments may contain filesystem paths. Output may contain tokens.
        raise SmokeFailure(f"Docker {arguments[0]} failed (exit {command.returncode}).")
    return command.stdout.strip()


def lower_keys(value: Any) -> Any:
    if isinstance(value, dict):
        return {key.lower(): lower_keys(item) for key, item in value.items()}
    if isinstance(value, list):
        return [lower_keys(item) for item in value]
    return value


class Client:
    def __init__(self, origin: str, base_path: str = "", token: str = "") -> None:
        self.origin = origin
        self.base_path = base_path
        self.token = token
        self.device = uuid.uuid4().hex

    def request(
        self, path: str, method: str = "GET", body: Any = None,
        expected: int | tuple[int, ...] = 200, headers: dict[str, str] | None = None,
        raw: bool = False,
    ) -> Any:
        authorization = f'MediaBrowser Client="JellySin smoke", Device="Test", DeviceId="{self.device}", Version="1.0.0"'
        if self.token:
            authorization += f', Token="{self.token}"'
        outgoing = {"Authorization": authorization, "Accept": "application/json"}
        if headers:
            outgoing.update(headers)
        data = None if body is None else json.dumps(body).encode()
        if data is not None:
            outgoing["Content-Type"] = "application/json"
        display_path = "/Auth/Keys/[redacted]" if path.startswith("/Auth/Keys/") else path.split("?")[0]
        request = urllib.request.Request(self.origin + self.base_path + path, data=data, headers=outgoing, method=method)
        try:
            response = urllib.request.urlopen(request, timeout=20)
        except urllib.error.HTTPError as error:
            response = error
        except (OSError, http.client.HTTPException) as error:
            raise SmokeFailure(f"{method} {display_path} failed with {type(error).__name__}.") from None
        with response:
            payload = response.read(MAX_RESPONSE + 1)
            status, incoming = response.status, dict(response.headers)
        require(len(payload) <= MAX_RESPONSE, "HTTP response exceeded the smoke-test bound.")
        allowed = (expected,) if isinstance(expected, int) else expected
        require(status in allowed, f"{method} {display_path} returned HTTP {status}; expected {allowed}.")
        if raw:
            return payload, {key.lower(): value for key, value in incoming.items()}, status
        return lower_keys(json.loads(payload)) if payload else None

    def authenticate(self, username: str, password: str) -> dict[str, Any]:
        result = self.request("/Users/AuthenticateByName", "POST", {"Username": username, "Pw": password})
        self.token = result["accesstoken"]
        return result["user"]


def wait_ready(client: Client) -> None:
    deadline = time.monotonic() + 180
    while time.monotonic() < deadline:
        try:
            client.request("/System/Info/Public")
            client.request("/Startup/Configuration", expected=(200, 401, 403), raw=True)
            return
        except (SmokeFailure, urllib.error.URLError, TimeoutError, ConnectionError):
            time.sleep(0.5)
    raise SmokeFailure("Jellyfin did not become ready within 180 seconds.")


def fixture_music(directory: Path, title: str) -> None:
    directory.mkdir(parents=True, exist_ok=True)
    path = directory / (title + ".wav")
    with wave.open(str(path), "wb") as audio:
        audio.setnchannels(1)
        audio.setsampwidth(2)
        audio.setframerate(8000)
        audio.writeframes(b"\0\0" * 8000 * 65)
    tags = b"INFO"
    for name, value in ((b"INAM", title), (b"IART", "JellySin Fixture Artist"), (b"IPRD", "Smoke Album")):
        encoded = value.encode() + b"\0"
        tags += name + struct.pack("<I", len(encoded)) + encoded + (b"\0" if len(encoded) % 2 else b"")
    with path.open("r+b") as audio:
        audio.seek(0, os.SEEK_END)
        audio.write(b"LIST" + struct.pack("<I", len(tags)) + tags)
        size = audio.tell()
        audio.seek(4)
        audio.write(struct.pack("<I", size - 8))


class DockerHost:
    def __init__(self, plugin: Path, image: str) -> None:
        self.name = "jellysin-smoke-" + uuid.uuid4().hex[:16]
        self.config = self.name + "-config"
        self.cache = self.name + "-cache"
        self.image = image
        self.plugin = plugin
        self.origin = ""
        self.started = False
        self.volumes: list[str] = []
        self.fixture_directory = Path(tempfile.mkdtemp(prefix=self.name + "-"))

    def start(self) -> None:
        try:
            docker("image", "inspect", self.image)
        except SmokeFailure:
            print("Pulling the pinned Jellyfin image.", flush=True)
            docker("pull", self.image, timeout=300)
        for name in (self.config, self.cache):
            docker("volume", "create", "--label", "org.jellysin.smoke=true", name)
            self.volumes.append(name)
        for library, title in (("Public", "JellySin Public Fixture"), ("Hidden", "JellySin Hidden Fixture")):
            fixture_music(self.fixture_directory / library, title)
        docker("run", "--rm", "--network", "none", "--user", "0:0", "--entrypoint", "/bin/sh",
               "--mount", f"type=volume,src={self.config},dst=/config",
               "--mount", f"type=volume,src={self.cache},dst=/cache", self.image,
               "-c", "mkdir -p /config/plugins/JellySin.Lastfm && chown -R 1000:1000 /config /cache")
        docker("run", "--detach", "--name", self.name, "--label", "org.jellysin.smoke=true",
               "--user", "1000:1000", "--memory", "1536m", "--cpus", "2", "--stop-timeout", "20",
               "--publish", "127.0.0.1::8096", "--mount", f"type=volume,src={self.config},dst=/config",
               "--mount", f"type=volume,src={self.cache},dst=/cache",
               "--mount", f"type=bind,src={self.plugin},dst=/config/plugins/JellySin.Lastfm/JellySin.Plugin.Lastfm.dll,readonly",
               "--mount", f"type=bind,src={self.fixture_directory},dst=/media,readonly", self.image)
        self.started = True
        self.refresh_origin()
        wait_ready(Client(self.origin))

    def refresh_origin(self) -> None:
        info = json.loads(docker("inspect", self.name))[0]
        port = info["NetworkSettings"]["Ports"]["8096/tcp"][0]["HostPort"]
        self.origin = "http://127.0.0.1:" + port

    def restart(self, base_path: str) -> None:
        docker("restart", self.name)
        self.refresh_origin()
        wait_ready(Client(self.origin, base_path))

    def cleanup(self) -> None:
        failures = []
        if self.started:
            try:
                docker("rm", "--force", self.name)
            except SmokeFailure:
                failures.append("container")
        for name in self.volumes:
            try:
                docker("volume", "rm", name)
            except SmokeFailure:
                failures.append("volume")
        # The directory was created by this instance; no user-supplied path is removed.
        for path in self.fixture_directory.rglob("*"):
            if path.is_file():
                path.unlink()
        for path in sorted(self.fixture_directory.rglob("*"), reverse=True):
            if path.is_dir():
                path.rmdir()
        self.fixture_directory.rmdir()
        require(not failures, "Some uniquely owned Docker smoke resources could not be removed.")


def setup_server(client: Client, password: str) -> dict[str, Any]:
    print("Configuring the isolated server's startup wizard.", flush=True)
    client.request("/Startup/Configuration", "POST", {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"}, 204)
    client.request("/Startup/User")
    client.request("/Startup/User", "POST", {"Name": "SmokeAdmin", "Password": password}, 204)
    client.request("/Startup/RemoteAccess", "POST", {"EnableRemoteAccess": True, "EnableAutomaticPortMapping": False}, 204)
    client.request("/Startup/Complete", "POST", expected=204)
    return client.authenticate("SmokeAdmin", password)


def check_plugin(client: Client) -> None:
    plugins = client.request("/Plugins")
    plugin = next((item for item in plugins if item["id"].replace("-", "") == PLUGIN_ID.replace("-", "")), None)
    require(plugin is not None and plugin["status"].lower() == "active", "The new plugin is not active in Jellyfin.")
    check_page_redirect(client)
    page, headers, _ = client.request(PREFIX + "/", raw=True)
    require(b"JellySin" in page and b"Assets/app.js" in page, "Plugin page or compiled application entrypoint is absent.")
    require(headers.get("cache-control") == "no-store", "Plugin page must not be cached.")
    require(headers.get("x-content-type-options") == "nosniff", "Plugin page must reject MIME sniffing.")
    require("frame-ancestors 'none'" in headers.get("content-security-policy", ""), "Plugin page CSP is missing.")
    require(headers.get("referrer-policy") == "no-referrer", "Plugin page must prevent referrer leakage.")
    for asset in ("app.js", "client.js", "auth.js", "account.js", "music.js", "favourites.js", "playlists.js", "view.js", "style.css"):
        data, _, _ = client.request(PREFIX + "/Assets/" + asset, raw=True)
        require(len(data) > 0, "A required embedded asset is empty.")
    client.request(PREFIX + "/Assets/not-a-real-file.js", expected=404, raw=True)
    bootstrap = client.request(PREFIX + "/Bootstrap")
    require(bootstrap["basepath"] == client.base_path, "The plugin does not report Jellyfin's configured base path.")
    page, _, _ = client.request("/web/configurationpage?name=jellysin-lastfm", raw=True)
    require(b"JellySin" in page, "The plugin's administrator configuration page is missing.")


def check_page_redirect(client: Client) -> None:
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, request: Any, file: Any, code: int, message: str, headers: Any, new_url: str) -> None:
            return None

    opener = urllib.request.build_opener(NoRedirect)
    try:
        response = opener.open(client.origin + client.base_path + PREFIX, timeout=20)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        require(response.status == 302, "The page URL without a trailing slash must redirect.")
        require(response.headers.get("Location") == client.base_path + PREFIX + "/", "The page redirect lost its configured base path.")


def create_user(admin: Client, password: str) -> dict[str, Any]:
    user = admin.request("/Users/New", "POST", {"Name": "SmokeListener", "Password": password})
    require(not user["policy"]["isadministrator"], "The ordinary smoke user unexpectedly has administrator rights.")
    return user


def check_authentication(admin: Client, listener: Client, listener_id: str, expect_configured: bool) -> None:
    anonymous = Client(admin.origin, admin.base_path)
    anonymous.request(PREFIX + "/Me", expected=401, raw=True)
    configured = admin.request(PREFIX + "/Admin/Application")["configured"]
    require(configured is expect_configured, "The Last.fm application configuration does not match this test's expected build.")
    admin.request(PREFIX + "/Admin/Application", "PUT", {"ApiKey": "invalid", "Secret": "invalid"}, 400, raw=True)
    listener.request(PREFIX + "/Admin/Application", expected=403, raw=True)
    identity = listener.request(PREFIX + "/Me")
    require(identity["username"] == "SmokeListener" and not identity["isadministrator"], "Ordinary-user identity is incorrect.")
    listener.request(PREFIX + "/Me/Favourites", "PUT", {"Enabled": False}, 403, {"Origin": "https://foreign.invalid"}, raw=True)
    listener.request(PREFIX + "/Me/Favourites", "PUT", {"Enabled": False}, 403, {"Sec-Fetch-Site": "cross-site"}, raw=True)
    admin.request("/Auth/Keys?app=JellySinSmoke", "POST", expected=204)
    keys = admin.request("/Auth/Keys")
    key = next(item["accesstoken"] for item in keys["items"] if item["appname"] == "JellySinSmoke")
    Client(admin.origin, admin.base_path, key).request(PREFIX + "/Me", expected=401, raw=True)
    admin.request("/Auth/Keys/" + urllib.parse.quote(key, safe=""), "DELETE", expected=204)
    require(admin.request("/QuickConnect/Enabled") is True, "Quick Connect is unavailable in the isolated server.")
    attempt = anonymous.request("/QuickConnect/Initiate", "POST")
    anonymous.request(PREFIX + "/Auth/QuickConnect/Status", "POST", {"Secret": "short"}, 400, raw=True)
    anonymous.request(PREFIX + "/Auth/QuickConnect/Status", "POST", {"Secret": attempt["secret"]}, 403, {"Origin": "https://foreign.invalid"}, raw=True)
    status = anonymous.request(PREFIX + "/Auth/QuickConnect/Status", "POST", {"Secret": attempt["secret"]})
    require(not status["authenticated"], "A new Quick Connect request was already authorized.")
    query = urllib.parse.urlencode({"code": attempt["code"], "userId": listener_id})
    admin.request("/QuickConnect/Authorize?" + query, "POST")
    status = anonymous.request(PREFIX + "/Auth/QuickConnect/Status", "POST", {"Secret": attempt["secret"]})
    require(status["authenticated"], "Quick Connect authorization was not recognized by the plugin.")
    session = anonymous.request("/Users/AuthenticateWithQuickConnect", "POST", {"Secret": attempt["secret"]})
    quick = Client(admin.origin, admin.base_path, session["accesstoken"])
    require(quick.request(PREFIX + "/Me")["username"] == "SmokeListener", "Quick Connect authenticated the wrong user.")
    quick.request("/Sessions/Logout", "POST", expected=204)
    quick.request(PREFIX + "/Me", expected=401, raw=True)


def check_user_isolation(admin: Client, listener: Client, admin_id: str) -> None:
    # Connecting Last.fm is intentionally omitted. Opt-in intent is stored before
    # the absent-account error, which provides distinct private state to inspect.
    admin.request(PREFIX + "/Me/Favourites", "PUT", {"Enabled": True}, 409, raw=True)
    require(admin.request(PREFIX + "/Me/Favourites")["enabled"], "Administrator favourite preference did not persist.")
    require(not listener.request(PREFIX + "/Me/Favourites")["enabled"], "Administrator state leaked to another user.")
    listener.request(PREFIX + "/Me/Favourites", "PUT", {"Enabled": False, "UserId": admin_id})
    require(admin.request(PREFIX + "/Me/Favourites")["enabled"], "A body-supplied identity changed another user's settings.")
    require(not listener.request(PREFIX + "/Me/Favourites")["enabled"], "The caller's own preference changed unexpectedly.")
    admin.request(PREFIX + "/Me/Favourites", "PUT", {"Enabled": False})
    listener.request(PREFIX + "/Me/History/Import", "POST", {"PreviewId": str(uuid.uuid4())}, 409, raw=True)
    listener.request(PREFIX + "/Me/Playlists", "POST", {"Id": str(uuid.UUID(int=0)), "Name": "", "Source": 0, "Limit": 0}, 400, raw=True)


def plugin_settings(admin: Client, **changes: bool) -> dict[str, Any]:
    path = "/Plugins/" + PLUGIN_ID + "/Configuration"
    configuration = json.loads(admin.request(path, raw=True)[0])
    configuration.update(changes)
    admin.request(path, "POST", configuration, 204)
    actual = admin.request(path)
    require(all(actual[name.lower()] == value for name, value in changes.items()), "Plugin settings did not round-trip.")
    return actual


def add_library(admin: Client, name: str) -> None:
    query = urllib.parse.urlencode({"name": "Smoke " + name, "collectionType": "music", "refreshLibrary": "false"})
    options = {"PathInfos": [{"Path": "/media/" + name}], "EnableRealtimeMonitor": False,
               "EnableInternetProviders": False, "EnableEmbeddedTitles": True,
               "TypeOptions": [{"Type": kind, "MetadataFetchers": [], "ImageFetchers": []} for kind in ("MusicArtist", "MusicAlbum", "Audio")]}
    admin.request("/Library/VirtualFolders?" + query, "POST", {"LibraryOptions": options}, 204)


def wait_music(admin: Client) -> list[dict[str, Any]]:
    for _ in range(120):
        items = admin.request("/Items?recursive=true&includeItemTypes=Audio&fields=ProviderIds&limit=20")["items"]
        if len(items) >= 2:
            return items
        time.sleep(0.5)
    raise SmokeFailure("Fixture music was not scanned within 60 seconds.")


def check_library(admin: Client, listener: Client, user: dict[str, Any]) -> str:
    plugin_settings(admin, Enabled=False, MetadataEnabled=False, SimilarityEnabled=False)
    for name in ("Public", "Hidden"):
        add_library(admin, name)
    admin.request("/Library/Refresh", "POST", expected=204)
    tracks = wait_music(admin)
    plugin_settings(admin, Enabled=True, MetadataEnabled=False, SimilarityEnabled=False)
    require(any("Public" in item["name"] for item in tracks), "The embedded fixture title was not scanned.")
    folders = admin.request("/Library/VirtualFolders")
    public = next(folder["itemid"] for folder in folders if folder["name"] == "Smoke Public")
    policy = user["policy"]
    policy.update({"enableallfolders": False, "enabledfolders": [public]})
    admin.request("/Users/" + user["id"] + "/Policy", "POST", policy, 204)
    visible = listener.request(PREFIX + "/Me/Library/Search?query=JellySin")
    require(any("Public" in item["name"] for item in visible), "The ordinary user cannot search accessible music.")
    require(not any("Hidden" in item["name"] for item in visible), "The plugin search leaked a restricted library.")
    secret = next(item["id"] for item in tracks if "Hidden" in item["name"])
    listener.request(PREFIX + "/Me/Discovery?seedItemId=" + secret, expected=(403, 404), raw=True)
    return next(item["id"] for item in tracks if "Public" in item["name"])


def check_disabled_playback(admin: Client, listener: Client, track: str) -> None:
    plugin_settings(admin, Enabled=False, MetadataEnabled=False, SimilarityEnabled=False)
    playing = {"ItemId": track, "PositionTicks": 0, "IsPaused": False, "PlayMethod": "DirectPlay", "CanSeek": True,
               "PlaySessionId": uuid.uuid4().hex, "MediaSourceId": track}
    listener.request("/Sessions/Playing", "POST", playing, 204)
    listener.request("/Sessions/Playing/Progress", "POST", {**playing, "PositionTicks": 400_000_000, "EventName": "TimeUpdate"}, 204)
    listener.request("/Sessions/Playing/Stopped", "POST", {**playing, "PositionTicks": 650_000_000}, 204)
    delivery = listener.request(PREFIX + "/Me/Delivery")
    require(delivery["pending"] == 0 and delivery["failedwrites"] == 0, "Disabled playback unexpectedly queued or failed a scrobble.")
    plugin_settings(admin, Enabled=True, MetadataEnabled=False, SimilarityEnabled=False)


@contextlib.contextmanager
def reverse_proxy(target: str):
    upstream = urllib.parse.urlsplit(target)

    class Proxy(http.server.BaseHTTPRequestHandler):
        def log_message(self, _format: str, *args: Any) -> None:
            pass

        def forward(self) -> None:
            length = int(self.headers.get("Content-Length", "0"))
            if length > 16_384:
                self.send_error(413)
                return
            body = self.rfile.read(length) if length else None
            headers = {name: value for name, value in self.headers.items() if name.lower() not in ("connection", "transfer-encoding")}
            connection = http.client.HTTPConnection(upstream.hostname, upstream.port, timeout=20)
            try:
                connection.request(self.command, self.path, body, headers)
                response = connection.getresponse()
                payload = response.read(MAX_RESPONSE + 1)
                self.send_response(response.status)
                for name, value in response.headers.items():
                    if name.lower() not in ("connection", "transfer-encoding", "content-length", "server", "date"):
                        self.send_header(name, value)
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)
            finally:
                connection.close()

        do_GET = forward
        do_POST = forward
        do_PUT = forward
        do_DELETE = forward

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Proxy)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield "http://127.0.0.1:" + str(server.server_port)
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)


def check_base_path(host: DockerHost, admin: Client, listener: Client) -> None:
    configuration = json.loads(admin.request("/System/Configuration/network", raw=True)[0])
    configuration["BaseUrl"] = "/jellyfin"
    admin.request("/System/Configuration/network", "POST", configuration, 204)
    host.restart("/jellyfin")
    admin.origin = listener.origin = host.origin
    admin.base_path = listener.base_path = "/jellyfin"
    check_plugin(admin)
    require(not admin.request("/Plugins/" + PLUGIN_ID + "/Configuration")["metadataenabled"], "Settings were lost after restart.")
    require(not listener.request(PREFIX + "/Me/Favourites")["enabled"], "Private preference was lost after restart.")
    with reverse_proxy(host.origin) as origin:
        proxy = Client(origin, "/jellyfin", listener.token)
        require(proxy.request(PREFIX + "/Bootstrap")["basepath"] == "/jellyfin", "Reverse-proxy prefix was not preserved.")
        require(proxy.request(PREFIX + "/Me")["username"] == "SmokeListener", "Header authentication failed through the proxy.")
        proxy.request(PREFIX + "/Me/Favourites", "PUT", {"Enabled": False}, headers={"Origin": origin})
        proxy.request(PREFIX + "/Me/Favourites", "PUT", {"Enabled": False}, 403, {"Origin": "https://foreign.invalid"}, raw=True)


def run_smoke(host: DockerHost, expect_configured: bool = False) -> dict[str, Any]:
    print("Starting isolated Jellyfin 12 as UID 1000.", flush=True)
    host.start()
    admin_password, listener_password = secrets.token_urlsafe(24), secrets.token_urlsafe(24)
    admin = Client(host.origin)
    admin_user = setup_server(admin, admin_password)
    check_plugin(admin)
    print("PASS: plugin activation, embedded assets, security headers and configuration page.", flush=True)
    user = create_user(admin, listener_password)
    listener = Client(host.origin)
    listener.authenticate("SmokeListener", listener_password)
    check_authentication(admin, listener, user["id"], expect_configured)
    check_user_isolation(admin, listener, admin_user["id"])
    print("PASS: password/Quick Connect, anonymous/API-key/origin rejection and user isolation.", flush=True)
    track = check_library(admin, listener, user)
    check_disabled_playback(admin, listener, track)
    print("PASS: real music scan, restricted-library access and disabled-playback events.", flush=True)
    check_base_path(host, admin, listener)
    print("PASS: /jellyfin base path, reverse proxy and restart persistence.", flush=True)
    return {"container": host.name, "configVolume": host.config, "cacheVolume": host.cache,
            "fixtureDirectory": str(host.fixture_directory), "url": host.origin + "/jellyfin/JellySin/Lastfm/", "applicationConfigured": expect_configured,
            "admin": {"id": admin_user["id"], "username": "SmokeAdmin", "password": admin_password, "token": admin.token},
            "listener": {"id": user["id"], "username": "SmokeListener", "password": listener_password, "token": listener.token},
            "fixture": {"itemId": track, "artist": "JellySin Fixture Artist", "title": "JellySin Public Fixture",
                        "album": "Smoke Album", "durationSeconds": 65}}


def arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plugin", type=Path)
    parser.add_argument("--image", default=DEFAULT_IMAGE)
    parser.add_argument("--keep", action="store_true", help="Keep the isolated server after a successful run.")
    parser.add_argument("--state-file", type=Path, help="Owner-only access details; requires --keep.")
    parser.add_argument("--expect-app-configured", action="store_true", help="Verify a production DLL with the embedded project application.")
    parser.add_argument("--cleanup-state", type=Path, help="Remove only the isolated resources recorded in a retained smoke state file.")
    args = parser.parse_args()
    if args.cleanup_state:
        if args.plugin or args.keep or args.state_file or args.expect_app_configured:
            parser.error("--cleanup-state cannot be combined with test options")
        return args
    if args.plugin is None:
        parser.error("--plugin is required for a test run")
    args.plugin = args.plugin.resolve(strict=True)
    if args.plugin.suffix.lower() != ".dll":
        parser.error("--plugin must name the built plugin DLL")
    if args.keep != bool(args.state_file):
        parser.error("--keep and --state-file must be used together")
    if "@sha256:" not in args.image:
        parser.error("--image must be pinned to a digest")
    if args.state_file and args.state_file.exists():
        parser.error("--state-file must not already exist")
    return args


def cleanup_retained(path: Path) -> None:
    state = json.loads(path.read_text(encoding="utf-8"))
    name = state["container"]
    require(isinstance(name, str) and re.fullmatch(r"jellysin-smoke-[a-f0-9]{16}", name), "The state file does not describe a smoke-test container.")
    info = json.loads(docker("inspect", name))[0]
    require(info["Config"]["Labels"].get("org.jellysin.smoke") == "true", "The retained container lacks the smoke ownership label.")
    fixture = Path(state["fixtureDirectory"]).resolve()
    require(fixture.parent == Path(tempfile.gettempdir()).resolve() and fixture.name.startswith(name + "-"), "The fixture directory is outside its owned temporary location.")
    volumes = [name + "-config", name + "-cache"]
    for volume in volumes:
        info = json.loads(docker("volume", "inspect", volume))[0]
        require(info["Labels"].get("org.jellysin.smoke") == "true", "A retained volume lacks the smoke ownership label.")
    host = object.__new__(DockerHost)
    host.name, host.started, host.volumes, host.fixture_directory = name, True, volumes, fixture
    host.cleanup()
    path.unlink()
    print("Removed the retained smoke container, its two volumes, fixtures and private state file.", flush=True)


def write_private_state(path: Path, state: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    try:
        if os.name == "nt":
            owner = subprocess.run(["whoami"], capture_output=True, text=True, check=True).stdout.strip()
            result = subprocess.run(["icacls", str(path), "/inheritance:r", "/grant:r", owner + ":(F)"],
                                    capture_output=True, check=False)
            require(result.returncode == 0, "Could not restrict access to the local smoke state file.")
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            descriptor = -1
            json.dump(state, stream, indent=2)
            stream.write("\n")
    except BaseException:
        if descriptor != -1:
            os.close(descriptor)
        path.unlink(missing_ok=True)
        raise


def main() -> int:
    args = arguments()
    if args.cleanup_state:
        try:
            cleanup_retained(args.cleanup_state)
            return 0
        except (SmokeFailure, OSError, ValueError, KeyError) as error:
            print("Cleanup failed: " + (str(error) if isinstance(error, SmokeFailure) else type(error).__name__), flush=True)
            return 1
    host = DockerHost(args.plugin, args.image)
    kept = False
    try:
        state = run_smoke(host, args.expect_app_configured)
        if args.keep:
            write_private_state(args.state_file, state)
            kept = True
            print("Smoke passed. The isolated server is retained; access details are in the requested state file.", flush=True)
        else:
            print("Smoke passed. Removing its isolated Docker resources.", flush=True)
        return 0
    except (SmokeFailure, urllib.error.URLError, TimeoutError, OSError, ValueError, KeyError) as error:
        safe = str(error) if isinstance(error, SmokeFailure) else type(error).__name__
        print("Smoke failed: " + safe, flush=True)
        return 1
    finally:
        if not kept:
            host.cleanup()


if __name__ == "__main__":
    raise SystemExit(main())
