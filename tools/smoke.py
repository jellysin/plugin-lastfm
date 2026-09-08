"""Exercise a packaged plugin inside a disposable, loopback-only Jellyfin server."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid
import wave
import zipfile

ROOT = Path(__file__).resolve().parents[1]
GUID = "5e7fe7f0-b048-429e-a431-b1a7e69c930d"


def command(*args):
    result = subprocess.run(args, check=True, capture_output=True, text=True, encoding="utf-8")
    return result.stdout.strip()


def smoke(image, archive):
    version_match = re.fullmatch(r"lastfm_(\d+\.\d+\.\d+\.\d+)\.zip", archive.name)
    if version_match is None:
        raise ValueError("Expected a versioned lastfm_M.m.p.r.zip package")
    expected_version = version_match.group(1)
    work_root = (ROOT / "build/smoke").resolve()
    work_root.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="server-", dir=work_root)).resolve()
    if not work.is_relative_to(work_root):
        raise ValueError("Smoke data directory escaped the workspace")
    container = "lastfm-smoke-" + uuid.uuid4().hex[:12]
    report = {"image": image, "archive": archive.name, "archiveSha256": hashlib.sha256(archive.read_bytes()).hexdigest(), "checks": []}
    token = None
    base_url = ""

    def request(method, route, data=None, authenticated=True, expected=200):
        authorization = 'MediaBrowser Client="Lastfm smoke", Device="Smoke", DeviceId="lastfm-smoke", Version="1.0"'
        if authenticated and token:
            authorization += ', Token="' + token + '"'
        headers = {"Authorization": authorization, "Content-Type": "application/json", "Accept": "application/json; profile=\"PascalCase\""}
        payload = json.dumps(data).encode() if data is not None else None
        req = urllib.request.Request(base_url + route, data=payload, headers=headers, method=method)
        try:
            with urllib.request.urlopen(req, timeout=30) as response:
                status, body = response.status, response.read()
        except urllib.error.HTTPError as error:
            status, body = error.code, error.read()
        allowed = (expected,) if isinstance(expected, int) else expected
        if status not in allowed:
            # Never print HTTP bodies, authorization headers or generated credentials.
            raise AssertionError(f"{method} {route.split('?')[0]} returned {status}, expected {allowed}")
        if not body:
            return None
        try:
            # Jellyfin endpoints may return either documented JSON casing profile.
            return json.loads(body, object_hook=lambda value: {key[:1].upper() + key[1:]: item for key, item in value.items()})
        except json.JSONDecodeError:
            return body.decode("utf-8")

    def wait_ready():
        deadline = time.monotonic() + 120
        while time.monotonic() < deadline:
            try:
                request("GET", "/Plugins" if token else "/Startup/Configuration", authenticated=bool(token))
                info = request("GET", "/System/Info/Public", authenticated=False)
                if isinstance(info, dict) and "Version" in info:
                    return info
                time.sleep(1)
            except (OSError, AssertionError):
                time.sleep(1)
        raise TimeoutError("Jellyfin did not become ready within 120 seconds")

    def container_url():
        binding = json.loads(command("docker", "inspect", container))[0]["NetworkSettings"]["Ports"]["8096/tcp"][0]
        return "http://127.0.0.1:" + binding["HostPort"]

    try:
        for name in ("config", "cache", "media", "plugin"):
            (work / name).mkdir()
        with zipfile.ZipFile(archive) as package:
            if package.namelist() != ["Jellyfin.Plugin.Lastfm.dll"]:
                raise ValueError("The plugin package must contain only the plugin DLL")
            (work / "plugin/Jellyfin.Plugin.Lastfm.dll").write_bytes(package.read("Jellyfin.Plugin.Lastfm.dll"))
        args = ["docker", "run", "--detach", "--name", container, "--publish", "127.0.0.1::8096"]
        if hasattr(os, "getuid"):
            # Bind-mounted output must remain removable by the invoking Linux user.
            args += ["--user", f"{os.getuid()}:{os.getgid()}"]
        for name in ("config", "cache", "media"):
            args += ["--mount", f"type=bind,source={work / name},target=/{name}"]
        # Jellyfin writes meta.json alongside the assembly during plugin discovery.
        args += ["--mount", f"type=bind,source={work / 'plugin'},target=/config/plugins/Lastfm", image]
        command(*args)
        base_url = container_url()
        info = wait_ready()
        report["serverVersion"] = info["Version"]
        report["imageId"] = command("docker", "inspect", "--format", "{{.Image}}", container)
        print("Ready:", image, flush=True)

        request("POST", "/Startup/Configuration", {"UICulture": "en-US", "MetadataCountryCode": "US", "PreferredMetadataLanguage": "en"}, authenticated=False, expected=204)
        # This startup endpoint initializes the first user on a fresh database.
        request("GET", "/Startup/User", authenticated=False)
        password = secrets.token_urlsafe(32)
        request("POST", "/Startup/User", {"Name": "smoke-admin", "Password": password}, authenticated=False, expected=204)
        request("POST", "/Startup/RemoteAccess", {"EnableRemoteAccess": False, "EnableAutomaticPortMapping": False}, authenticated=False, expected=204)
        request("POST", "/Startup/Complete", authenticated=False, expected=204)
        auth = request("POST", "/Users/AuthenticateByName", {"Username": "smoke-admin", "Pw": password}, authenticated=False)
        token = auth["AccessToken"]
        admin_id = auth["User"]["Id"]
        plugins = request("GET", "/Plugins")
        plugin = next(plugin for plugin in plugins if plugin["Id"].replace("-", "").lower() == GUID.replace("-", ""))
        if plugin["Status"] != "Active":
            raise AssertionError("Plugin did not load as Active: " + plugin["Status"])
        report["pluginVersion"] = plugin["Version"]
        if report["pluginVersion"] != expected_version:
            raise AssertionError("Loaded plugin version does not match the package filename")
        report["checks"].append("packaged DLL loads as Active")

        request("POST", "/Lastfm/Login", {}, authenticated=False, expected=401)
        request("POST", "/Lastfm/Login", {}, expected=400)
        request("POST", "/Users/New", {"Name": "smoke-listener"})
        listener_auth = request("POST", "/Users/AuthenticateByName", {"Username": "smoke-listener", "Pw": ""}, authenticated=False)
        admin_token = token
        token = listener_auth["AccessToken"]
        request("POST", "/Lastfm/Login", {}, expected=403)
        token = admin_token
        report["checks"].append("login rejects anonymous/non-admin callers and validates admin input")

        route = f"/Plugins/{GUID}/Configuration"
        configuration = request("GET", route)
        configuration["LastfmUsers"] = [{"MediaBrowserUserId": admin_id, "Username": "fixture-account", "SessionKey": "", "Options": {"Scrobble": False, "SyncFavourites": False, "AlternativeMode": False}}]
        request("POST", route, configuration, expected=204)
        if request("GET", route)["LastfmUsers"] != configuration["LastfmUsers"]:
            raise AssertionError("Configuration did not round-trip")
        page = request("GET", "/web/configurationpage?name=lastfm")
        if 'id="lastfmPassword" type="password"' not in page:
            raise AssertionError("Embedded configuration page was not served")
        report["checks"].append("embedded dashboard and XML configuration round-trip")

        tasks = request("GET", "/ScheduledTasks")
        task = next(task for task in tasks if task["Key"] == "ImportLastfmData")
        request("POST", "/ScheduledTasks/Running/" + task["Id"], expected=204)
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            state = request("GET", "/ScheduledTasks/" + task["Id"])
            if (state.get("LastExecutionResult") or {}).get("Status") == "Completed":
                break
            time.sleep(0.25)
        else:
            raise AssertionError("Import task did not finish successfully")
        report["checks"].append("scheduled import completes with no connected accounts")

        with wave.open(str(work / "media/source.wav"), "wb") as audio:
            audio.setnchannels(1)
            audio.setsampwidth(2)
            audio.setframerate(8000)
            audio.writeframes(b"\x00\x00" * (8000 * 61))
        command("docker", "exec", container, "/usr/lib/jellyfin-ffmpeg/ffmpeg", "-hide_banner", "-loglevel", "error", "-i", "/media/source.wav", "-metadata", "artist=Smoke Artist", "-metadata", "album=Smoke Album", "-metadata", "title=Smoke Track", "/media/track.mp3")
        (work / "media/source.wav").unlink()
        options = {"PathInfos": [{"Path": "/media"}], "EnableRealtimeMonitor": False, "EnableInternetProviders": False, "MetadataOptions": [{"ItemType": item, "MetadataFetchers": [], "ImageFetchers": []} for item in ("MusicArtist", "MusicAlbum", "Audio")]}
        request("POST", "/Library/VirtualFolders?name=Smoke&collectionType=music&refreshLibrary=true", {"LibraryOptions": options}, expected=204)
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            items = request("GET", f"/Items?userId={admin_id}&includeItemTypes=Audio&recursive=true")["Items"]
            if items:
                break
            time.sleep(1)
        else:
            raise AssertionError("Fixture audio was not scanned")
        item_id = items[0]["Id"]
        request("POST", f"/Users/{admin_id}/FavoriteItems/{item_id}")
        request("DELETE", f"/Users/{admin_id}/FavoriteItems/{item_id}")
        playback = {"ItemId": item_id, "PlaySessionId": uuid.uuid4().hex, "PositionTicks": 0, "CanSeek": True, "IsPaused": False}
        request("POST", "/Sessions/Playing", playback, expected=204)
        playback["PositionTicks"] = 60 * 10_000_000
        request("POST", "/Sessions/Playing/Progress", playback, expected=204)
        request("POST", "/Sessions/Playing/Stopped", playback, expected=204)
        report["checks"].append("music scan, favourite events, and playback events with scrobbling disabled")

        command("docker", "restart", container)
        # Docker can reassign an automatically allocated host port on restart.
        base_url = container_url()
        wait_ready()
        if request("GET", route)["LastfmUsers"] != configuration["LastfmUsers"]:
            raise AssertionError("Configuration changed after restart")
        report["checks"].append("configuration survives server restart")
        print(json.dumps(report, indent=2), flush=True)
        safe_version = report["serverVersion"].replace(".", "-")
        (work_root / f"result-{safe_version}.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        return report
    except Exception:
        # Keep diagnostic server logs local, outside version control. Do not print them.
        logs = subprocess.run(["docker", "logs", container], capture_output=True, text=True, encoding="utf-8")
        (work_root / "last-failure.log").write_text(logs.stdout + logs.stderr, encoding="utf-8")
        raise
    finally:
        subprocess.run(["docker", "rm", "--force", container], check=False, capture_output=True)
        if work.is_relative_to(work_root) and work.name.startswith("server-"):
            shutil.rmtree(work)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", required=True)
    parser.add_argument("--archive", type=Path, required=True)
    args = parser.parse_args()
    smoke(args.image, args.archive.resolve())
