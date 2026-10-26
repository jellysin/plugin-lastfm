#!/usr/bin/env python3
"""Start the Last.fm sign-in in a real browser, on a server behind a proxy that ends TLS.

The deployment under test: Jellyfin answers plain HTTP, a proxy in front of it ends TLS, the server is
told its published address (https) and trusts no proxy, so it sees every request as plain HTTP. The
browser opens the published address and, as a second address, the server's own HTTP port.

Nothing here contacts Last.fm: the browser's request for the approval page is answered by a stub.

The server log is part of the result: every click leaves the lines named in EXPECTED and nothing else, so a
refusal in the browser can be read on the server afterwards, and no line carries what a browser sent.
"""

from __future__ import annotations

import argparse
import contextlib
import hashlib
import http.client
import http.server
import json
from pathlib import Path
import re
import secrets
import ssl
import subprocess
import sys
import tempfile
import threading
import time
from typing import Any, Iterator
import urllib.parse
import uuid

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
from smoke import (  # noqa: E402
    PREFIX, Client, DockerHost, SmokeFailure, add_server_options, chosen_image, create_user, docker, require, setup_server,
)

# A name no resolver knows: the browser is told to reach it at the proxy's local port.
HOST = "jellyfin.test"
PUBLISHED = "https://" + HOST
MAX_BODY = 64_000_000
HOP_HEADERS = ("connection", "transfer-encoding", "keep-alive", "upgrade")

# What the plugin's request handling writes to the server log, per scenario of the browser half, in order.
BEGUN = ("INF", "Last.fm sign-in begun for user {user} from {page}. The return address uses https; "
         "the request reached this server over http; the page is {place} the published server address.")
NOTED = ("INF", "The page did not open Last.fm for user {user}: the return address the server names is not at the address of the page. "
         "The request reached this server over http; the page is not at the published server address.")
REFUSED = ("WRN", "Refused PUT JellySin/Lastfm/Me/Favourites: its origin is neither the published server address nor the address of the request. "
           "The request reached this server over http; the page is not at the published server address.")
EXPECTED: dict[str, list[tuple[tuple[str, str], str, str, str]]] = {
    "published address, user page, ordinary user": [(BEGUN, "listener", "the music dashboard", "at")],
    "published address, user page, a saved preference": [],
    "published address, settings page, administrator": [(BEGUN, "admin", "the settings page", "at")],
    "published address, settings page, a refused request names its status": [],
    "second address, user page, administrator": [(BEGUN, "admin", "the music dashboard", "not at"), (NOTED, "admin", "", "")],
    "second address, user page, ordinary user": [(BEGUN, "listener", "the music dashboard", "not at"), (NOTED, "listener", "", "")],
    "second address, settings page, administrator": [(BEGUN, "admin", "the settings page", "not at"), (NOTED, "admin", "", "")],
}
LINE = re.compile(r"\[(?P<level>[A-Z]{3})\].*?JellySin\.Plugin\.Lastfm\.Api\.\w+: (?P<text>.*)$")


def log_name(user_id: str) -> str:
    """How the plugin names a user in its log: the start of a hash of the id, in the byte order the server keeps it."""
    return hashlib.sha256(uuid.UUID(user_id).bytes_le).hexdigest()[:12]


def expected_lines(scenarios: list[str], accounts: dict[str, Any]) -> list[tuple[str, str]]:
    unknown = [name for name in scenarios if name not in EXPECTED]
    require(not unknown, "No log lines are named for the scenario: " + ", ".join(unknown))
    return [(line[0], line[1].format(user=log_name(accounts[who]["id"]), page=page, place=place))
            for name in scenarios for line, who, page, place in EXPECTED[name]]


def plugin_lines(host: DockerHost) -> list[tuple[str, str]]:
    """The level and text of every line the plugin's request handling wrote; the server's own lines stay unread."""
    found = (LINE.search(line) for line in docker("logs", host.name).splitlines())
    return [(match["level"], match["text"].strip()) for match in found if match]


def settled_lines(host: DockerHost, expected: list[tuple[str, str]]) -> list[tuple[str, str]]:
    """The server writes its log from a queue, so a line may follow its request by a moment; a line too many needs no wait."""
    deadline = time.monotonic() + 15
    while (lines := plugin_lines(host)) != expected and len(lines) < len(expected) and time.monotonic() < deadline:
        time.sleep(0.5)
    return lines


def check_log(host: DockerHost, accounts: dict[str, Any], scenarios: list[str], report: Path) -> bool:
    """Every click left its lines and nothing else; a refused write and a made-up reason leave one line and none."""
    expected = expected_lines(scenarios, accounts)
    lines = settled_lines(host, expected)
    admin = Client(host.origin, token=accounts["admin"]["token"])
    admin.request(PREFIX + "/Me/Favourites", "PUT", {"Enabled": False}, 403, {"Origin": "https://foreign.invalid"}, raw=True)
    admin.request(PREFIX + "/Me/Connection/Refusal", "POST", {"Reason": "made-up"}, 400, raw=True)
    after = settled_lines(host, expected + [REFUSED])
    (report / "server-lines.txt").write_text("".join(f"[{level}] {text}\n" for level, text in after), encoding="utf-8")
    secret = [accounts[who][key] for who in ("admin", "listener") for key in ("id", "password", "token")]
    secret += [str(uuid.UUID(accounts[who]["id"])) for who in ("admin", "listener")] + [HOST, "127.0.0.1", "foreign.invalid", "made-up", "attempt=", "token="]
    passed = True
    for name, condition in (("the clicks left exactly the lines named for them", lines == expected),
                            ("a refused write left exactly one line with its reason, a made-up reason none", after[len(lines):] == [REFUSED]),
                            ("no line carries an address, an account or a secret", not any(value.lower() in text.lower() for _, text in after for value in secret))):
        print(("PASS" if condition else "FAIL") + ": server log: " + name, flush=True)
        passed = passed and condition
    if lines != expected:
        # The lines are fixed phrases and the start of a hash, so they can be shown.
        for title, shown in (("expected", expected), ("found", lines)):
            print(f"  {title}:" + "".join(f"\n    [{level}] {text}" for level, text in shown), flush=True)
    return passed


def certificate(directory: Path) -> ssl.SSLContext:
    """A certificate for the test name that lives for this run; the browser is told not to verify it."""
    key, chain = directory / "key.pem", directory / "certificate.pem"
    made = subprocess.run(
        ["openssl", "req", "-x509", "-newkey", "ec", "-pkeyopt", "ec_paramgen_curve:prime256v1", "-nodes", "-days", "2",
         "-keyout", str(key), "-out", str(chain), "-subj", "/CN=" + HOST, "-addext", "subjectAltName=DNS:" + HOST],
        capture_output=True, text=True, timeout=60, check=False)
    require(made.returncode == 0, "A certificate for the test name could not be made.")
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.load_cert_chain(chain, key)
    return context


@contextlib.contextmanager
def tls_proxy(target: str, context: ssl.SSLContext, seen: list[dict[str, Any]]) -> Iterator[int]:
    """End TLS and pass each request on as plain HTTP, the way a gateway in front of the server does."""
    upstream = urllib.parse.urlsplit(target)

    class Proxy(http.server.BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, _format: str, *args: Any) -> None:
            pass

        def handle(self) -> None:
            # A browser that does not know the certificate drops its first connection during the handshake.
            with contextlib.suppress(ssl.SSLError, ConnectionError):
                super().handle()

        def forward(self) -> None:
            length = int(self.headers.get("Content-Length", "0"))
            body = self.rfile.read(length) if 0 < length <= MAX_BODY else None
            # The host name the browser asked for passes unchanged; the scheme is only named in a header,
            # which a server that trusts no proxy ignores.
            headers = {name: value for name, value in self.headers.items() if name.lower() not in HOP_HEADERS}
            headers["X-Forwarded-Proto"] = "https"
            headers["X-Forwarded-For"] = self.client_address[0]
            connection = http.client.HTTPConnection(upstream.hostname, upstream.port, timeout=60)
            try:
                connection.request(self.command, self.path, body, headers)
                response = connection.getresponse()
                payload = response.read(MAX_BODY)
                # The record keeps no query string: the return from Last.fm carries a token there.
                seen.append({"method": self.command, "path": self.path.split("?")[0], "status": response.status})
                self.send_response(response.status)
                for name, value in response.headers.items():
                    if name.lower() not in (*HOP_HEADERS, "content-length", "server", "date"):
                        # A line break in a name or value would begin a header of its own.
                        self.send_header(name.replace("\r", "").replace("\n", ""), value.replace("\r", "").replace("\n", ""))
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                if self.command != "HEAD":
                    self.wfile.write(payload)
            except (OSError, http.client.HTTPException):
                self.close_connection = True
            finally:
                connection.close()

        do_GET = do_HEAD = do_POST = do_PUT = do_DELETE = forward

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Proxy)
    server.daemon_threads = True
    # The handshake runs in the thread of its connection, so one stalled connection does not hold up the others.
    server.socket = context.wrap_socket(server.socket, server_side=True, do_handshake_on_connect=False)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield server.server_port
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)


def seed(host: DockerHost, configured: bool) -> dict[str, Any]:
    """An administrator, an ordinary user and a Last.fm application; a session of each for the server's own web client."""
    admin_password, listener_password = secrets.token_urlsafe(24), secrets.token_urlsafe(24)
    admin = Client(host.origin)
    admin_user = setup_server(admin, admin_password)
    listener_user = create_user(admin, listener_password)
    listener = Client(host.origin)
    listener.authenticate("SmokeListener", listener_password)
    if not configured:
        # The server only builds an address from the application; nothing here contacts Last.fm.
        admin.request(PREFIX + "/Admin/Application", "PUT", {"ApiKey": secrets.token_hex(16), "Secret": secrets.token_hex(16)}, 204)
    return {
        "serverId": Client(host.origin).request("/System/Info/Public")["id"],
        "admin": {"name": "SmokeAdmin", "password": admin_password, "id": admin_user["id"], "token": admin.token},
        "listener": {"name": "SmokeListener", "password": listener_password, "id": listener_user["id"], "token": listener.token},
    }


def read_results(path: Path, status: int) -> list[dict[str, Any]]:
    """What the browser half wrote; nothing when it ended on an error of its own, which is a failure and not a crash of this half."""
    if status in (0, 1) and path.is_file():
        return json.loads(path.read_text(encoding="utf-8"))
    print(f"FAIL: the browser half ended with status {status} and left no result.", flush=True)
    return []


def run(host: DockerHost, configured: bool, report: Path, only: str) -> int:
    print("Starting isolated Jellyfin 12 with the published address " + PUBLISHED + " and no trusted proxy.", flush=True)
    host.start()
    accounts = seed(host, configured)
    seen: list[dict[str, Any]] = []
    with tempfile.TemporaryDirectory(prefix="jellysin-proxied-") as private:
        with tls_proxy(host.origin, certificate(Path(private)), seen) as port:
            settings = Path(private) / "run.json"
            settings.touch(mode=0o600)
            settings.write_text(json.dumps({**accounts, "host": HOST, "published": PUBLISHED, "proxyPort": port,
                                            "direct": host.origin, "report": str(report), "only": only}), encoding="utf-8")
            # The result of an earlier run must not stand in for a browser half that ended without one.
            (report / "result.json").unlink(missing_ok=True)
            browser = subprocess.run(["node", str(Path(__file__).with_suffix(".mjs")), str(settings)], cwd=ROOT, timeout=600, check=False)
    results = read_results(report / "result.json", browser.returncode)
    logged = bool(results) and check_log(host, accounts, [result["name"] for result in results], report)
    writes = [entry for entry in seen if entry["path"].startswith(PREFIX) and entry["method"] != "GET"]
    (report / "requests-through-proxy.json").write_text(json.dumps(writes, indent=1) + "\n", encoding="utf-8")
    for entry in writes:
        print(f"through the proxy: {entry['method']} {entry['path']} -> {entry['status']}", flush=True)
    return browser.returncode if browser.returncode or logged else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plugin", type=Path, required=True)
    add_server_options(parser)
    parser.add_argument("--expect-app-configured", action="store_true", help="The DLL carries the project's Last.fm application.")
    parser.add_argument("--report", type=Path, required=True, help="A directory for the result, the screenshots of failures and the requests the proxy passed on.")
    parser.add_argument("--only", default="", help="Run only the scenarios whose name contains this text.")
    args = parser.parse_args()
    plugin = args.plugin.resolve(strict=True)
    require(plugin.name == "JellySin.Plugin.Lastfm.dll", "Name the built plugin DLL.")
    args.report.mkdir(parents=True, exist_ok=True)
    host = DockerHost(plugin, chosen_image(parser, args), published=PUBLISHED)
    try:
        code = run(host, args.expect_app_configured, args.report.resolve(), args.only)
    finally:
        host.cleanup()
    print("PASS: the sign-in starts from the published address, is refused with both addresses elsewhere, and the server log names each." if code == 0
          else "FAIL: see the scenarios above.", flush=True)
    return code


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except SmokeFailure as failure:
        print("FAIL: " + str(failure), file=sys.stderr)
        raise SystemExit(1) from None
