#!/usr/bin/env python3
"""Verify the Last.fm settings row and dialog in Jellyfin web across releases.

The server runs from a pinned image (tools/smoke.py) in Docker. The test creates
an ordinary listener user, signs into the Jellyfin web client, and runs
tests/integration/settings_entry.mjs in a headless browser to confirm:
1. The Last.fm row appears in the user settings menu (#/mypreferencesmenu).
2. Clicking the row opens the Last.fm view without requiring a sign-in.

With --plant moved-layout, the browser injects a mutation observer that renames
DOM classes to simulate a restructured settings page; the test requires the
settings row check to fail.
"""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import tempfile
from typing import Any
import urllib.error

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))

from smoke import (  # noqa: E402
    Client, DockerHost, SmokeFailure, add_server_options, chosen_image, require, setup_server,
)


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plugin", type=Path, required=True, help="Path to built plugin DLL.")
    parser.add_argument("--evidence", type=Path, required=True, help="Directory for screenshots and logs.")
    parser.add_argument(
        "--plant", choices=("none", "moved-layout"), default="none",
        help="Planted failure mode to test detection (default: none).",
    )
    add_server_options(parser)
    args = parser.parse_args()
    args.plugin = args.plugin.resolve(strict=True)
    if args.plugin.suffix.lower() != ".dll":
        parser.error("--plugin must name the built plugin DLL")
    args.image = chosen_image(parser, args)
    return args


def create_listener_user(admin: Client) -> tuple[dict[str, Any], Client]:
    listener_password = secrets.token_hex(16)
    user = admin.request("/Users/New", "POST", {"Name": "EntryListener", "Password": listener_password})
    require(not user["policy"]["isadministrator"], "Listener user unexpectedly has administrator rights.")
    client = Client(admin.origin)
    client.authenticate("EntryListener", listener_password)
    return user, client


def write_run_configuration(directory: Path, state: dict[str, Any]) -> Path:
    run_file = directory / "run.json"
    descriptor = os.open(run_file, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
    with open(descriptor, "w", encoding="utf-8") as handle:
        json.dump(state, handle, indent=2)
        handle.write("\n")
    run_file.chmod(0o600)
    return run_file


def save_server_log(host: DockerHost, evidence: Path) -> None:
    log = subprocess.run(
        ["docker", "logs", host.name],
        capture_output=True, text=True, encoding="utf-8",
        errors="replace", timeout=60, check=False,
    )
    (evidence / "server.log").write_text(log.stdout + log.stderr, encoding="utf-8")


def evaluate_browser_result(result_path: Path, exit_code: int, plant: str) -> None:
    require(result_path.exists(), "result.json was not created by settings_entry.mjs")
    result = json.loads(result_path.read_text(encoding="utf-8"))
    results = result.get("results", [])
    if plant == "none":
        require(exit_code == 0, f"Browser run failed with exit code {exit_code}")
        require(len(results) == 2 and all(item.get("passed") for item in results), "A settings entry check failed.")
        print(f"PASS: Jellyfin {result.get('server')}: settings entry verified.", flush=True)
        return
    if plant == "moved-layout":
        require(exit_code != 0, "Planted layout change unexpectedly exited with 0.")
        require(len(results) >= 1, "No check results recorded in result.json.")
        row_check = next((item for item in results if item.get("name") == "Last.fm row in user settings"), None)
        require(row_check is not None and not row_check.get("passed", True), "Expected row check failure was not reported.")
        failed_checks = [item for item in results if not item.get("passed", True)]
        require(len(failed_checks) == 1 and failed_checks[0].get("name") == "Last.fm row in user settings",
                f"Unexpected checks failed: {[c.get('name') for c in failed_checks]}")
        detail = row_check.get("detail", "")
        print(f"PASS: planted layout change refused: {detail}", flush=True)
        return
    raise SmokeFailure(f"Unsupported plant mode: {plant}")


def run(image: str, plugin: Path, evidence: Path, plant: str) -> None:
    evidence.mkdir(parents=True, exist_ok=True)
    host = DockerHost(plugin, image)
    try:
        host.start()
        admin = Client(host.origin)
        admin_password = secrets.token_hex(16)
        setup_server(admin, admin_password)
        listener_user, listener_client = create_listener_user(admin)
        server_id = admin.request("/System/Info/Public")["id"]
        with tempfile.TemporaryDirectory(prefix="jellysin-settings-entry-") as temp_dir:
            run_file = write_run_configuration(Path(temp_dir), {
                "origin": host.origin,
                "serverId": server_id,
                "listener": {"id": listener_user["id"], "token": listener_client.token},
                "evidence": str(evidence.resolve()),
                "version": host.version,
                "plant": plant,
            })
            try:
                browser = subprocess.run(
                    ["node", str(ROOT / "tests/integration/settings_entry.mjs"), str(run_file)],
                    cwd=ROOT, timeout=300, check=False,
                )
                evaluate_browser_result(evidence / "result.json", browser.returncode, plant)
            finally:
                save_server_log(host, evidence)
    finally:
        host.cleanup()


def main() -> int:
    args = parse_arguments()
    try:
        run(args.image, args.plugin, args.evidence, args.plant)
        return 0
    except (SmokeFailure, urllib.error.URLError, TimeoutError, subprocess.TimeoutExpired, OSError, ValueError, KeyError) as error:
        safe = str(error) if isinstance(error, SmokeFailure) else type(error).__name__
        print(f"FAIL: {safe}", file=sys.stderr, flush=True)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
