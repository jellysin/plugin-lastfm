#!/usr/bin/env python3
"""Install a published release from the Plugin Repository on a fresh server, restart it twice, and record the result.

The release records in docs/validation run this once the release's catalog entry has merged. It starts a disposable
pinned Jellyfin 12 server with no plugin, adds the published catalog through the server's repository API and installs
the named version through its package API, the same calls as catalog_over_canary.py, which serves a local catalog
instead. It passes only when, after each of two restarts, the server lists that version alone and active.

The report names the catalog, the image, the server release, the plugin listing before the install, after it and
after each restart, and the SHA-256 of the installed assembly. --plant none-installed judges the listing from before
the install in place of an installed one, a planted failure the check must refuse.
"""

from __future__ import annotations

import argparse
import json
import re
import secrets
import sys
import urllib.parse
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
sys.path.insert(0, str(Path(__file__).resolve().parent))
from catalog_over_canary import DESCRIPTOR, install, listed, restart  # noqa: E402
from smoke import Client, DockerHost, SmokeFailure, add_server_options, chosen_image, docker, require, setup_server  # noqa: E402

CATALOG = "https://raw.githubusercontent.com/jellysin/repo/main/manifest.json"
# The server downloads the catalog and the release archive from the internet, which takes longer than a local catalog.
PACKAGE_SECONDS = 120
RESTARTS = 2


def catalog_address(text: str) -> str:
    """Accepts an HTTPS catalog address with a host; anything else is an argument error."""
    parts = urllib.parse.urlsplit(text)
    if parts.scheme != "https" or not parts.hostname:
        raise argparse.ArgumentTypeError(f"the catalog must be an HTTPS address with a host, not {text!r}")
    return text


def catalog_version(text: str) -> str:
    """Accepts a four-part catalog version such as 0.5.0.0; a tag name or a three-part version is an argument error."""
    if not re.fullmatch(r"(?:0|[1-9][0-9]{0,8})(?:\.(?:0|[1-9][0-9]{0,8})){3}", text):
        raise argparse.ArgumentTypeError(f"the version must have four numeric parts, as in 0.5.0.0, not {text!r}")
    return text


def judge(entries: list[dict[str, Any]], version: str, when: str) -> None:
    """Requires the server to list the plugin once, at version and active."""
    require(entries == [{"name": DESCRIPTOR["name"], "version": version, "status": "Active"}],
            f"{when} the server lists {entries}, not {version} alone and active.")


def assembly_digests(host: DockerHost) -> list[str]:
    """The SHA-256 and path of every installed copy of the plugin assembly."""
    script = f"sha256sum /config/plugins/*/{DESCRIPTOR['assemblyFile']}"
    return docker("exec", host.name, "sh", "-c", script, timeout=60).splitlines()


def run(host: DockerHost, args: argparse.Namespace, report: dict[str, Any]) -> None:
    """Fills report step by step, so a failed run still records what the server did up to there."""
    password = secrets.token_urlsafe(24)
    host.start()
    report.update({"image": host.image, "server": host.version})
    admin = Client(host.origin)
    setup_server(admin, password)
    report["before"] = listed(admin)
    if args.plant == "none-installed":
        judge(report["before"], args.version, "Before the install")
    install(admin, args.catalog, args.version, PACKAGE_SECONDS)
    report["installed"] = listed(admin)
    report["assembly-sha256"] = assembly_digests(host)
    for number in range(1, RESTARTS + 1):
        admin = restart(host, password)
        report[f"after-restart-{number}"] = listed(admin)
        judge(report[f"after-restart-{number}"], args.version, f"After restart {number}")
    report["passed"] = True


def main() -> int:
    """Runs the install against a disposable server and writes its report; 0 on pass, 1 on failure."""
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--version", type=catalog_version, required=True, help="The catalog version to install, such as 0.5.0.0.")
    parser.add_argument("--catalog", type=catalog_address, default=CATALOG, help=f"The catalog manifest (default: {CATALOG}).")
    parser.add_argument("--report", type=Path, required=True, help="Where to write the JSON record of the run.")
    parser.add_argument("--plant", choices=("none-installed",), help="Plant a defect the check must refuse.")
    add_server_options(parser)
    args = parser.parse_args()
    image = chosen_image(parser, args)
    report: dict[str, Any] = {"catalog": args.catalog, "version": args.version, "plant": args.plant}
    host = DockerHost(None, image)
    try:
        run(host, args, report)
        print(f"PASS: Jellyfin {host.version} installed {args.version} from {args.catalog} and kept it over {RESTARTS} restarts.")
    except SmokeFailure as failure:
        report["failure"] = str(failure)
        print(f"FAIL: {failure}", file=sys.stderr, flush=True)
    finally:
        if host.started:
            report["log"] = [line for line in docker("logs", host.name).splitlines() if "lugin" in line][-60:]
        args.report.parent.mkdir(parents=True, exist_ok=True)
        args.report.write_text(json.dumps(report, indent=1) + "\n", encoding="utf-8")
        host.cleanup()
    return 0 if report.get("passed") else 1


if __name__ == "__main__":
    raise SystemExit(main())
