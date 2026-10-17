#!/usr/bin/env python3
"""Install a release-shaped package from a plugin repository over a canary copied in by hand, and record the result.

A canary of main (tools/canary.py) has no meta.json and is copied into a plugin folder by hand; a release comes from
a Plugin Repository into a folder Jellyfin names itself. Both carry the same plugin GUID. This check starts a
disposable Jellyfin 12 server with a canary-shaped build in its plugin folder, connects an ordinary user through
the Last.fm stand-in of the page check (made-up application, no real account), then serves a repository manifest
and a release-shaped archive from a container on the run's network, installs the release through Jellyfin's own
package API and restarts. It passes only when, after the restart, the server lists exactly one JellySin plugin,
active at the release version, the canary folder no longer loads, the user is still connected and a favourite
reaches the stand-in, which needs the stored session key read back with the kept key ring.

The report names the image, the server release, the plugin folders and their meta.json before the install, after
it and after the restart, and every JellySin entry the server listed. --plant keys removes the plugin's key ring
before the restart, a planted loss of state the check must refuse.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import secrets
import sys
import tempfile
import time
from typing import Any
import urllib.parse
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
from page_check import STAND_IN_IMAGE, StandInContainer  # noqa: E402
from smoke import (  # noqa: E402
    PLUGIN_ID, PREFIX, Client, DockerHost, SmokeFailure, add_library, add_server_options, chosen_image, create_user, docker,
    fixture_music, remove_container, require, setup_server,
)
from state_carryover import approve, love, require_connected, signed_in  # noqa: E402

DESCRIPTOR = json.loads((ROOT / "plugin.json").read_text(encoding="utf-8"))
# The folder a canary is copied into; the same name the other checks mount the plugin under.
CANARY_FOLDER = "JellySin.Lastfm"
RELEASE_VERSION = "0.1.0.0"
REPOSITORY_ALIAS = "plugin-repository"
REPOSITORY_PORT = 8080
MANIFEST_URL = f"http://{REPOSITORY_ALIAS}:{REPOSITORY_PORT}/manifest.json"
ARCHIVE = "plugin-lastfm-0.1.0.zip"
PACKAGE_SECONDS = 60
RESTART_SECONDS = 180
FAVOURITES = ("Catalog Before", "Catalog After")
# The favourites and the seed track every server writes into the Public library.
LIBRARY_TRACKS = len(FAVOURITES) + 1


def release_archive(release: Path, directory: Path) -> str:
    """Writes the archive as the release helper packages one: stored entries, the assembly and LICENSE. Returns its MD5."""
    path = directory / ARCHIVE
    with zipfile.ZipFile(path, "w", zipfile.ZIP_STORED) as bundle:
        for name, data in ((DESCRIPTOR["assemblyFile"], release.read_bytes()), ("LICENSE", (ROOT / "LICENSE").read_bytes())):
            entry = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
            entry.external_attr = 0o100644 << 16
            bundle.writestr(entry, data)
    return hashlib.md5(path.read_bytes(), usedforsecurity=False).hexdigest()


def write_repository(release: Path, directory: Path) -> None:
    checksum = release_archive(release, directory)
    version = {"version": RELEASE_VERSION, "changelog": "Release-shaped package of the catalog check.",
               "targetAbi": DESCRIPTOR["targetAbi"], "sourceUrl": f"http://{REPOSITORY_ALIAS}:{REPOSITORY_PORT}/{ARCHIVE}",
               "checksum": checksum, "timestamp": "2026-10-17T00:00:00Z"}
    plugin = {key: DESCRIPTOR[key] for key in ("guid", "name", "description", "overview", "owner", "category")}
    (directory / "manifest.json").write_text(json.dumps([{**plugin, "versions": [version]}], indent=1) + "\n", encoding="utf-8")


class Repository:
    """The plugin repository container on the stand-in's network; removed again by cleanup."""

    def __init__(self, directory: Path, network: str) -> None:
        self.directory, self.network = directory, network
        self.name = network + "-repository"
        self.started = False

    def start(self) -> None:
        docker("run", "--detach", "--name", self.name, "--label", "org.jellysin.smoke=true", "--network", self.network,
               "--network-alias", REPOSITORY_ALIAS, "--user", "65534:65534", "--memory", "128m", "--cpus", "1", "--read-only",
               "--mount", f"type=bind,src={self.directory},dst=/repository,readonly", STAND_IN_IMAGE,
               "python", "-I", "-m", "http.server", str(REPOSITORY_PORT), "--directory", "/repository")
        self.started = True

    def cleanup(self) -> None:
        require(not self.started or remove_container(self.name), "The plugin repository container could not be removed.")


def server_arguments(stand_in: StandInContainer) -> tuple[str, ...]:
    """The stand-in's options, with the repository reached directly instead of through the Last.fm proxy."""
    arguments = list(stand_in.server_arguments())
    index = arguments.index("NO_PROXY=localhost,127.0.0.1")
    arguments[index] += "," + REPOSITORY_ALIAS
    return tuple(arguments)


def place_canary(canary: Path, image: str, volume: str) -> None:
    """Copies the canary into the plugin folder of a new configuration volume, as an administrator would by hand."""
    docker("volume", "create", "--label", "org.jellysin.smoke=true", volume)
    target = f"/config/plugins/{CANARY_FOLDER}"
    docker("run", "--rm", "--network", "none", "--user", "0:0", "--entrypoint", "/bin/sh",
           "--mount", f"type=volume,src={volume},dst=/config", "--mount", f"type=bind,src={canary},dst=/canary.dll,readonly",
           image, "-c", f"mkdir -p {target} && cp /canary.dll {target}/{DESCRIPTOR['assemblyFile']} && chown -R 1000:1000 /config")


def folders(host: DockerHost) -> dict[str, Any]:
    """Every plugin folder with its files and the version and status its meta.json names."""
    script = ("cd /config/plugins && for d in */; do d=${d%/}; [ \"$d\" = configurations ] && continue; "
              "printf '%s\\t%s\\t' \"$d\" \"$(ls \"$d\" | tr '\\n' ' ')\"; "
              "if [ -f \"$d/meta.json\" ]; then tr -d '\\n' < \"$d/meta.json\"; fi; echo; done")
    result = {}
    for line in docker("exec", host.name, "sh", "-c", script).splitlines():
        name, files, meta = (line.split("\t") + ["", ""])[:3]
        manifest = json.loads(meta) if meta else {}
        if not files.split():
            # The empty fixture folder every DockerHost prepares for the native probe.
            continue
        result[name] = {"files": files.split(), "meta": {key: manifest.get(key) for key in ("id", "name", "version", "status")}}
    return result


def state_folders(host: DockerHost) -> list[str]:
    script = "find /config -mindepth 1 -maxdepth 4 -type d -path '*/jellysin-lastfm/*' | sort"
    return docker("exec", host.name, "sh", "-c", script).split()


def listed(admin: Client) -> list[dict[str, Any]]:
    """The server's own view of every JellySin entry."""
    return [{key: item[key] for key in ("name", "version", "status")} for item in admin.request("/Plugins")
            if item["id"].replace("-", "") == PLUGIN_ID.replace("-", "")]


def snapshot(host: DockerHost, admin: Client) -> dict[str, Any]:
    return {"folders": folders(host), "plugins": listed(admin), "state": state_folders(host)}


def connect(host: DockerHost, stand_in: StandInContainer, password: str, admin: Client | None = None) -> Client:
    if admin is None:
        admin = Client(host.origin)
        setup_server(admin, password)
    for title in FAVOURITES:
        fixture_music(host.fixture_directory / "Public", title)
    add_library(admin, "Public")
    admin.request("/Library/Refresh", "POST", expected=204)
    admin.request(PREFIX + "/Admin/Application", "PUT", {"ApiKey": "a" * 32, "Secret": "b" * 32}, expected=(200, 204))
    create_user(admin, password)
    listener = signed_in(host, password)
    approve(listener)
    love(listener, stand_in, FAVOURITES[0], first=True, tracks=LIBRARY_TRACKS)
    return admin


def install(admin: Client) -> None:
    """Adds the repository and installs the release from it through Jellyfin's package API."""
    admin.request("/Repositories", "POST", [{"Name": "JellySin catalog check", "Url": MANIFEST_URL, "Enabled": True}], expected=204)
    deadline = time.monotonic() + PACKAGE_SECONDS
    while not any(item["guid"].replace("-", "") == PLUGIN_ID.replace("-", "") for item in admin.request("/Packages")):
        require(time.monotonic() < deadline, "The server does not list the package of the local repository.")
        time.sleep(1)
    query = urllib.parse.urlencode({"assemblyGuid": DESCRIPTOR["guid"], "version": RELEASE_VERSION, "repositoryUrl": MANIFEST_URL})
    admin.request("/Packages/Installed/" + urllib.parse.quote(DESCRIPTOR["name"]) + "?" + query, "POST", expected=204,
                  timeout=PACKAGE_SECONDS)


def restart(host: DockerHost, password: str) -> Client:
    docker("restart", host.name, timeout=120)
    host.refresh_origin()
    for _ in range(RESTART_SECONDS):
        try:
            return signed_in(host, password, "SmokeAdmin")
        except SmokeFailure:
            time.sleep(1)
    raise SmokeFailure("The server did not come back after the restart.")


def judge_listing(after: dict[str, Any]) -> None:
    """The server's view after the restart: the release alone and active, and the canary folder not loading."""
    require(after["plugins"] == [{"name": DESCRIPTOR["name"], "version": RELEASE_VERSION, "status": "Active"}],
            f"After the restart the server lists {after['plugins']}, not the release alone and active.")
    canary = after["folders"].get(CANARY_FOLDER)
    require(canary is None or canary["meta"]["status"] != "Active", f"The canary folder still loads: {canary}.")


def judge(report: dict[str, Any], stand_in: StandInContainer, host: DockerHost, password: str) -> None:
    judge_listing(report["after-restart"])
    # The plugin creates an empty key ring at start, so only a love with the stored session key shows it was kept.
    listener = signed_in(host, password)
    require_connected(listener, "the catalog install")
    love(listener, stand_in, FAVOURITES[1], first=False, tracks=LIBRARY_TRACKS)


def run(host: DockerHost, stand_in: StandInContainer, plant: str | None, report: dict[str, Any]) -> None:
    """Fills report step by step, so a failed run still records what the server did up to there."""
    password = secrets.token_urlsafe(24)
    host.start()
    report.update({"image": host.image, "server": host.version})
    admin = connect(host, stand_in, password)
    report["canary"] = snapshot(host, admin)
    install(admin)
    report["installed"] = snapshot(host, admin)
    if plant == "keys":
        docker("exec", host.name, "sh", "-c", "find /config -type d -path '*/jellysin-lastfm/keys' -exec rm -r {} +")
    report["after-restart"] = snapshot(host, restart(host, password))
    judge(report, stand_in, host, password)
    report["passed"] = True


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--canary", type=Path, required=True, help="A canary-shaped build of the plugin assembly.")
    parser.add_argument("--release", type=Path, required=True, help="A release-shaped build of the plugin assembly.")
    parser.add_argument("--report", type=Path, required=True, help="Where to write the JSON record of the run.")
    parser.add_argument("--plant", choices=("keys",), help="Plant a defect the check must refuse.")
    add_server_options(parser)
    args = parser.parse_args()
    image = chosen_image(parser, args)
    report: dict[str, Any] = {"plant": args.plant}
    with tempfile.TemporaryDirectory(prefix="jellysin-catalog-") as name:
        for part in ("stand-in", "repository"):
            (Path(name) / part).mkdir(mode=0o755)
        write_repository(args.release.resolve(strict=True), Path(name) / "repository")
        stand_in = StandInContainer(Path(name) / "stand-in")
        repository = Repository(Path(name) / "repository", stand_in.network)
        host = DockerHost(None, image, extra_arguments=())
        try:
            stand_in.start()
            repository.start()
            host.extra_arguments = list(server_arguments(stand_in))
            place_canary(args.canary.resolve(strict=True), image, host.config)
            run(host, stand_in, args.plant, report)
            print(f"PASS: Jellyfin {host.version}: the catalog release replaced the canary and the account stayed connected.")
        except SmokeFailure as failure:
            report["failure"] = str(failure)
            print(f"FAIL: {failure}", file=sys.stderr, flush=True)
        finally:
            if host.started:
                report["log"] = [line for line in docker("logs", host.name).splitlines() if "lugin" in line][-60:]
            args.report.parent.mkdir(parents=True, exist_ok=True)
            args.report.write_text(json.dumps(report, indent=1) + "\n", encoding="utf-8")
            for part in (host, repository, stand_in):
                part.cleanup()
    return 0 if report.get("passed") else 1


if __name__ == "__main__":
    raise SystemExit(main())
