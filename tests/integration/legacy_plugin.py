#!/usr/bin/env python3
"""Load the real legacy Last.fm plugin next to JellySin on a disposable Jellyfin 12 host and require the pause.

The legacy plugin's last release (jesseward/jellyfin-plugin-lastfm 10.0.0.0) is downloaded from its GitHub release
into a temporary folder, checked against a pinned SHA-256 and mounted as an installed plugin. The check passes only
when the server lists that plugin as active and JellySin reports it in an ordinary user's delivery status, which is
what pauses listening. The download is not kept, shipped or committed.
"""

from __future__ import annotations

import argparse
import hashlib
import io
from pathlib import Path
import secrets
import sys
import tempfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
from smoke import PREFIX, Client, DockerHost, add_server_options, chosen_image, create_user, require, setup_server  # noqa: E402

LEGACY_URL = "https://github.com/jesseward/jellyfin-plugin-lastfm/releases/download/10.0.0.0/lastfm_10.0.0.0.zip"
LEGACY_SHA256 = "60d9d6dbe68490acd097445d3ac23fc1f4a32a2aea4b393893b965e68f7f3030"
LEGACY_DLL = "Jellyfin.Plugin.Lastfm.dll"
LEGACY_MAX_BYTES = 1024 * 1024
# The Id the legacy assembly declares; a running server lists the plugin under it, not under its catalog GUID.
LEGACY_RUNTIME_ID = "de7fe7f0b048439ea431b1a7e99c930d"


def legacy_assembly() -> bytes:
    with urllib.request.urlopen(LEGACY_URL, timeout=60) as response:
        archive = response.read(LEGACY_MAX_BYTES + 1)
    require(len(archive) <= LEGACY_MAX_BYTES, "The legacy plugin release is larger than expected.")
    require(hashlib.sha256(archive).hexdigest() == LEGACY_SHA256, "The legacy plugin release does not match its pinned SHA-256.")
    with zipfile.ZipFile(io.BytesIO(archive)) as bundle:
        members = [item for item in bundle.infolist() if Path(item.filename).name == LEGACY_DLL]
        require(len(members) == 1 and members[0].file_size <= LEGACY_MAX_BYTES, "The legacy release holds no single plugin assembly.")
        return bundle.read(members[0])


def check(host: DockerHost) -> str:
    admin = Client(host.origin)
    password = secrets.token_urlsafe(24)
    setup_server(admin, password)
    legacy = [item for item in admin.request("/Plugins") if item["id"] == LEGACY_RUNTIME_ID]
    require(len(legacy) == 1 and legacy[0]["status"] == "Active",
            f"The server does not list the legacy plugin as active: {[(item['name'], item['status']) for item in legacy]}.")
    create_user(admin, password)
    listener = Client(host.origin)
    listener.authenticate("SmokeListener", password)
    detected = listener.request(PREFIX + "/Me/Delivery")["legacyplugindetected"]
    require(detected is True, "JellySin does not report the active legacy plugin, so listening is not paused.")
    return f"legacy plugin {legacy[0]['name']} {legacy[0]['version']} active; JellySin reports it and pauses listening"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plugin", required=True, type=Path)
    add_server_options(parser)
    args = parser.parse_args()
    image = chosen_image(parser, args)
    with tempfile.TemporaryDirectory(prefix="jellysin-legacy-") as directory:
        folder = Path(directory) / "LastFM_10.0.0.0"
        folder.mkdir()
        (folder / LEGACY_DLL).write_bytes(legacy_assembly())
        # The server runs as another user and rewrites the plugin's manifest in this folder.
        folder.chmod(0o777)
        (folder / LEGACY_DLL).chmod(0o644)
        host = DockerHost(args.plugin.resolve(strict=True), image,
                          extra_arguments=("--mount", f"type=bind,src={folder},dst=/config/plugins/LastFM_10.0.0.0"))
        try:
            host.start()
            print(f"PASS: Jellyfin {host.version}: {check(host)}.", flush=True)
        finally:
            host.cleanup()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
