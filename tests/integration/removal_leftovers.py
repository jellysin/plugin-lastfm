#!/usr/bin/env python3
"""Connect a user, remove the plugin through Jellyfin's server API, and verify the leftovers.

Removing the plugin in Jellyfin deletes the plugin folder and assembly while preserving user
preferences, response caches and the protection key ring on disk. This check starts a disposable
Jellyfin 12 server with the plugin installed either by copying into the plugin directory (--source copy)
or through the Plugin Repository API (--source repository). It connects an ordinary user through the
Last.fm stand-in of the page check and delivers a library favourite love. It then removes the plugin
via DELETE /Plugins/{pluginId}/{version}, restarts the server, requires the plugin to be absent from
the server's plugin listing, stops the container, and inspects the stopped configuration volume with a
throwaway container.

It passes only when no plugin folder contains the assembly, configurations/JellySin.Plugin.Lastfm.xml
is present with non-zero size, jellysin-lastfm/state has at least one file, and jellysin-lastfm/keys
holds the non-empty key ring from the connected account. --plant skip-remove skips the removal API call,
a planted defect the check must refuse.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import secrets
import sys
import tempfile
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
from catalog_over_canary import (  # noqa: E402
    CANARY_FOLDER, DESCRIPTOR, LIBRARY_TRACKS, RELEASE_VERSION, Repository, connect, install, listed,
    place_canary, restart, server_arguments, write_repository,
)
from page_check import StandInContainer  # noqa: E402
from smoke import (  # noqa: E402
    Client, DockerHost, SmokeFailure, add_server_options, chosen_image, docker, require, setup_server,
)

LISTING_LINES = 1000
FIND_SCRIPT = (
    'cd /config && [ -d plugins ] && (cd plugins && find . -mindepth 1 -type f -printf "%P\\t%s\\n") ; '
    'for d in $(find /config -type d -name jellysin-lastfm 2>/dev/null); do '
    '(cd "$d/.." && find . -path "./jellysin-lastfm/*" -type f -printf "%P\\t%s\\n"); done'
)


def parse_leftovers(output: str) -> dict[str, int]:
    """Parses find output into a dictionary mapping relative paths to sizes in bytes."""
    listing: dict[str, int] = {}
    lines = output.splitlines()
    require(len(lines) <= LISTING_LINES, f"The leftover listing has {len(lines)} lines, more than {LISTING_LINES}.")
    for line in lines:
        line = line.strip()
        if not line:
            continue
        parts = line.split("\t")
        require(len(parts) == 2, f"Invalid find output line: {line}")
        path, size_str = parts[0], parts[1]
        try:
            listing[path] = int(size_str)
        except ValueError:
            raise SmokeFailure(f"Invalid size for leftover {path}: {size_str}") from None
    return listing


def leftovers(host: DockerHost, stopped: bool = False) -> dict[str, int]:
    """Lists leftover files under plugins and jellysin-lastfm by name and size, never reading content."""
    if stopped:
        output = docker("run", "--rm", "--network", "none", "--user", "0:0", "--entrypoint", "/bin/sh",
                        "--mount", f"type=volume,src={host.config},dst=/config,readonly", host.image,
                        "-c", FIND_SCRIPT, timeout=60)
    else:
        output = docker("exec", host.name, "sh", "-c", FIND_SCRIPT, timeout=60)
    return parse_leftovers(output)


def judge_leftovers(listing: dict[str, Any]) -> None:
    """Raises SmokeFailure if leftovers differ from expectations after plugin removal."""
    assembly = DESCRIPTOR["assemblyFile"]
    assembly_paths = [path for path in listing if path == assembly or path.endswith("/" + assembly)]
    require(not assembly_paths, f"Plugin assembly still present after removal: {assembly_paths}.")

    config_path = "configurations/JellySin.Plugin.Lastfm.xml"
    config_size = listing.get(config_path)
    if config_size is None and ("plugins/" + config_path) in listing:
        config_size = listing["plugins/" + config_path]
    require(config_size is not None, f"Configuration file {config_path} missing from leftovers.")
    require(isinstance(config_size, int) and config_size > 0,
            f"Configuration file {config_path} is empty or has invalid size: {config_size}.")

    state_files = [
        path for path, size in listing.items()
        if (path.startswith("jellysin-lastfm/state/") or "/jellysin-lastfm/state/" in path)
        and isinstance(size, int) and size >= 0
    ]
    require(len(state_files) >= 1, "Expected state files under jellysin-lastfm/state, none found.")

    key_files = {
        path: size for path, size in listing.items()
        if (path.startswith("jellysin-lastfm/keys/") or "/jellysin-lastfm/keys/" in path)
        and not path.endswith("/") and path != "jellysin-lastfm/keys"
    }
    require(len(key_files) >= 1, "Expected key files under jellysin-lastfm/keys (connected account), none found.")
    require(all(isinstance(size, int) and size > 0 for size in key_files.values()),
            f"Key files must have non-zero size recorded: {key_files}.")


def run(host: DockerHost, stand_in: StandInContainer, source: str, plant: str | None,
        report: dict[str, Any], build: Path | None = None) -> None:
    """Executes the removal leftover flow and records each step in report."""
    password = secrets.token_urlsafe(24)
    build_path = build or host.plugin
    if source == "copy":
        require(build_path is not None, "A build path is required for copy source.")
        place_canary(build_path, host.image, host.config)
        host.start()
        report.update({"image": host.image, "server": host.version})
        admin = connect(host, stand_in, password)
    else:
        host.start()
        report.update({"image": host.image, "server": host.version})
        admin = Client(host.origin)
        setup_server(admin, password)
        install(admin)
        admin = restart(host, password)
        admin = connect(host, stand_in, password, admin=admin)

    report["before"] = {"leftovers": leftovers(host), "plugins": listed(admin)}
    entries = listed(admin)
    require(len(entries) == 1, f"Expected 1 listed plugin entry before removal, found {len(entries)}: {entries}.")
    if plant != "skip-remove":
        admin.request(f"/Plugins/{DESCRIPTOR['guid']}/{entries[0]['version']}", "DELETE", expected=204)
    admin = restart(host, password)
    after_plugins = listed(admin)
    docker("stop", host.name, timeout=60)
    after_leftovers = leftovers(host, stopped=True)
    report["after"] = {"leftovers": after_leftovers, "plugins": after_plugins}
    # The listing is judged first, so the planted case shows the leftover judge refusing the assembly.
    judge_leftovers(after_leftovers)
    require(not after_plugins, f"Plugin still listed after removal and restart: {after_plugins}.")
    report["passed"] = True


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--build", type=Path, required=True, help="The plugin assembly build.")
    parser.add_argument("--source", choices=("copy", "repository"), default="copy",
                        help="How to place the plugin before removal: hand copy (default) or repository install.")
    parser.add_argument("--report", type=Path, required=True, help="Where to write the JSON record of the run.")
    parser.add_argument("--plant", choices=("skip-remove",), help="Plant a defect the check must refuse.")
    add_server_options(parser)
    args = parser.parse_args()
    image = chosen_image(parser, args)
    report: dict[str, Any] = {"source": args.source, "plant": args.plant}
    with tempfile.TemporaryDirectory(prefix="jellysin-leftovers-") as name:
        stand_in_dir = Path(name) / "stand-in"
        repo_dir = Path(name) / "repository"
        for part in (stand_in_dir, repo_dir):
            part.mkdir(mode=0o755)
        stand_in = StandInContainer(stand_in_dir)
        repository: Repository | None = None
        if args.source == "repository":
            write_repository(args.build.resolve(strict=True), repo_dir)
            repository = Repository(repo_dir, stand_in.network)
        host = DockerHost(None, image, extra_arguments=())
        try:
            stand_in.start()
            if repository is not None:
                repository.start()
            host.extra_arguments = list(server_arguments(stand_in))
            run(host, stand_in, args.source, args.plant, report, args.build.resolve(strict=True))
            print(f"PASS: Jellyfin {host.version}: removal removed assembly; settings, state and keys kept.")
        except SmokeFailure as failure:
            report["failure"] = str(failure)
            print(f"FAIL: {failure}", file=sys.stderr, flush=True)
        finally:
            if host.started:
                report.setdefault("image", host.image)
                if getattr(host, "version", None):
                    report.setdefault("server", host.version)
                try:
                    report["log"] = [line for line in docker("logs", host.name, timeout=30).splitlines() if "lugin" in line][-60:]
                except SmokeFailure:
                    pass
            args.report.parent.mkdir(parents=True, exist_ok=True)
            args.report.write_text(json.dumps(report, indent=1) + "\n", encoding="utf-8")
            for part in (host, repository, stand_in):
                if part is not None:
                    part.cleanup()
    return 0 if report.get("passed") else 1


if __name__ == "__main__":
    raise SystemExit(main())
