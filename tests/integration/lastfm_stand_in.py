#!/usr/bin/env python3
"""A Last.fm stand-in for page_check.py: a forward proxy that tunnels only to the Last.fm API host.

It runs in its own container on the page check's private Docker network, so the server under test reaches it
by name and no port opens on the workstation. The proxy ends TLS with the run's certificate and answers the
documented Last.fm methods with fixture names. Standard library only: the container image carries Python alone.

Usage: lastfm_stand_in.py DIRECTORY. DIRECTORY holds server.pem, server.key and session.txt; the stand-in
writes out/calls.log (one line per answered method), out/tunnels.log (each tunnel request and whether
it carried a request) and out/ready once it listens.
"""

from __future__ import annotations

import http.server
import json
from pathlib import Path
import ssl
import subprocess
import sys
import threading
import time
from typing import Any
import urllib.parse

API_HOST = "ws.audioscrobbler.com"
PORT = 3128
LASTFM_USER = "stand-in-listener"
ARTIST = "JellySin Fixture Artist"
SEED = "JellySin Public Fixture"
SIMILAR_LOCAL = "JellySin Similar Fixture"
SIMILAR_REMOTE = "Stand-in Similar Song"
NOW = int(time.time())


def track(title: str, artist: str = ARTIST, played: int | None = None, plays: int = 0) -> dict[str, Any]:
    value: dict[str, Any] = {"name": title, "artist": {"name": artist}, "album": {"#text": "Smoke Album"},
                             "url": "https://www.last.fm/music/" + urllib.parse.quote(artist) + "/_/" + urllib.parse.quote(title),
                             "playcount": str(plays), "mbid": ""}
    if played is not None:
        value["date"] = {"uts": str(played)}
    return value


def page(root: str, tracks: list[dict[str, Any]]) -> dict[str, Any]:
    return {root: {"@attr": {"page": "1", "totalPages": "1", "total": str(len(tracks))}, "track": tracks}}


def answer(method: str, session: str) -> dict[str, Any]:
    """What the stand-in returns for a Last.fm method: the shapes Last.fm documents, with fixture names."""
    url = "https://www.last.fm/music/" + urllib.parse.quote(ARTIST)
    answers: dict[str, Any] = {
        "auth.getSession": {"session": {"name": LASTFM_USER, "key": session, "subscriber": 0}},
        "user.getInfo": {"user": {"name": LASTFM_USER, "playcount": "1234", "artist_count": "56", "album_count": "78",
                                  "track_count": "90", "url": "https://www.last.fm/user/" + LASTFM_USER}},
        "user.getTopTracks": page("toptracks", [track(SEED, plays=42), track(SIMILAR_LOCAL, plays=7)]),
        "user.getTopArtists": {"topartists": {"artist": [{"name": ARTIST, "playcount": "49", "url": url, "mbid": ""}]}},
        "user.getTopAlbums": {"topalbums": {"album": [{"name": "Smoke Album", "artist": {"name": ARTIST}, "playcount": "49",
                                                       "url": url + "/Smoke+Album", "mbid": ""}]}},
        "user.getRecentTracks": page("recenttracks", [track(SEED, played=NOW - 600), track(SIMILAR_LOCAL, played=NOW - 1200)]),
        "user.getLovedTracks": page("lovedtracks", []),
        "track.getSimilar": {"similartracks": {"track": [track(SIMILAR_LOCAL), track(SIMILAR_REMOTE, "Stand-in Artist")]}},
        "artist.getSimilar": {"similarartists": {"artist": [{"name": "Stand-in Related Artist", "url": "https://www.last.fm/music/Stand-in+Related+Artist", "mbid": ""}]}},
        "artist.getTopAlbums": {"topalbums": {"album": [{"name": "Stand-in Album", "url": url + "/Stand-in+Album", "mbid": ""}]}},
        "artist.getTopTracks": page("toptracks", [track(SEED)]),
    }
    if method in ("track.love", "track.unlove"):
        return {}
    if method == "track.updateNowPlaying":
        return {"nowplaying": {"ignoredMessage": {"code": "0", "#text": ""}}}
    if method == "track.scrobble":
        return {"scrobbles": {"@attr": {"accepted": 1, "ignored": 0}, "scrobble": []}}
    return answers.get(method, {"error": 3, "message": "Invalid Method - No method with that name in this package"})


class Record:
    """Appends one line per event to a file in the output directory, so the run keeps it even if the stand-in dies."""

    lock = threading.Lock()
    directory = Path()

    @classmethod
    def write(cls, name: str, line: str) -> None:
        with cls.lock, (cls.directory / name).open("a", encoding="utf-8") as file:
            file.write(line + "\n")


class StandIn(http.server.BaseHTTPRequestHandler):
    """The Last.fm API on a TLS connection the proxy has already ended."""

    protocol_version = "HTTP/1.1"
    session = ""
    answered = False

    def log_message(self, _format: str, *args: Any) -> None:
        pass

    def do_GET(self) -> None:
        self.reply(urllib.parse.parse_qs(urllib.parse.urlsplit(self.path).query), False)

    def do_POST(self) -> None:
        length = min(int(self.headers.get("Content-Length") or 0), 1_000_000)
        self.reply(urllib.parse.parse_qs(self.rfile.read(length).decode("utf-8", "replace")), True)

    def reply(self, values: dict[str, list[str]], signed: bool) -> None:
        method = (values.get("method") or [""])[0]
        self.answered = True
        Record.write("calls.log", ("POST " if signed else "GET ") + method)
        body = json.dumps(answer(method, StandIn.session)).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


class Tunnel(http.server.BaseHTTPRequestHandler):
    """A forward proxy that opens a tunnel only to the Last.fm API and ends its TLS with the run's certificate."""

    protocol_version = "HTTP/1.1"
    context: ssl.SSLContext

    def log_message(self, _format: str, *args: Any) -> None:
        pass

    def do_CONNECT(self) -> None:
        if self.path != API_HOST + ":443":
            Record.write("tunnels.log", f"refused {self.path.rsplit(':', 1)[0]}")
            self.send_error(403)
            return
        # The closing line follows only when the server closes the tunnel; a pooled connection may outlive the run.
        Record.write("tunnels.log", f"opened {API_HOST}")
        self.send_response(200, "Connection established")
        self.end_headers()
        self.wfile.flush()
        try:
            connection = Tunnel.context.wrap_socket(self.connection, server_side=True)
        except (ssl.SSLError, OSError) as error:
            Record.write("tunnels.log", f"tls-failed {API_HOST} {type(error).__name__}: {str(error)[:160]}")
            self.close_connection = True
            return
        version = connection.version()
        # Under TLS 1.3 this side finishes the handshake before the client checks the certificate; a client that
        # rejects it closes the tunnel without a request, so only an answered request proves the trust.
        try:
            answered = StandIn(connection, self.client_address, self.server).answered
        except (ssl.SSLError, OSError) as error:
            Record.write("tunnels.log", f"no-request {API_HOST} {version} {type(error).__name__}: {str(error)[:160]}")
            self.close_connection = True
            return
        Record.write("tunnels.log", f"{'tls-ended' if answered else 'no-request'} {API_HOST} {version}")
        self.close_connection = True

    def do_GET(self) -> None:  # plain HTTP is never forwarded
        Record.write("tunnels.log", f"refused {urllib.parse.urlsplit(self.path).hostname or ''}")
        self.send_error(403)


def make_certificates(directory: Path) -> None:
    """A certificate authority for one run only (ca.pem) and a server certificate for the Last.fm API host."""
    def openssl(*arguments: str) -> None:
        subprocess.run(["openssl", *arguments], cwd=directory, check=True, capture_output=True, timeout=60)
    openssl("req", "-x509", "-newkey", "ec", "-pkeyopt", "ec_paramgen_curve:prime256v1", "-nodes", "-days", "1",
            "-subj", "/CN=JellySin page check stand-in", "-addext", "basicConstraints=critical,CA:TRUE,pathlen:0",
            "-addext", "keyUsage=critical,keyCertSign,cRLSign", "-keyout", "ca.key", "-out", "ca.pem")
    openssl("req", "-newkey", "ec", "-pkeyopt", "ec_paramgen_curve:prime256v1", "-nodes",
            "-subj", "/CN=" + API_HOST, "-keyout", "server.key", "-out", "server.csr")
    (directory / "server.ext").write_text(f"subjectAltName=DNS:{API_HOST}\nextendedKeyUsage=serverAuth\nkeyUsage=critical,digitalSignature\n"
                                          "basicConstraints=critical,CA:FALSE\nauthorityKeyIdentifier=keyid\nsubjectKeyIdentifier=hash\n", encoding="utf-8")
    openssl("x509", "-req", "-in", "server.csr", "-CA", "ca.pem", "-CAkey", "ca.key", "-CAcreateserial",
            "-days", "1", "-extfile", "server.ext", "-out", "server.pem")
    # Nothing may sign a further certificate with it once the run's one certificate exists.
    (directory / "ca.key").unlink()
    (directory / "ca.pem").chmod(0o644)


def bind(directory: Path, address: tuple[str, int]) -> http.server.ThreadingHTTPServer:
    """The proxy, listening but not yet serving, with its records in DIRECTORY/out."""
    Record.directory = directory / "out"
    StandIn.session = (directory / "session.txt").read_text(encoding="utf-8").strip()
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    context.load_cert_chain(directory / "server.pem", directory / "server.key")
    Tunnel.context = context
    server = http.server.ThreadingHTTPServer(address, Tunnel)
    server.daemon_threads = True
    return server


def main() -> int:
    # Every interface of its own container, which sits on the run's private network alone.
    server = bind(Path(sys.argv[1]), ("0.0.0.0", PORT))
    (Record.directory / "ready").write_text("listening\n", encoding="utf-8")
    server.serve_forever()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
