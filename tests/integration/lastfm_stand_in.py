#!/usr/bin/env python3
"""A Last.fm stand-in for page_check.py: a forward proxy that tunnels only to the Last.fm API host.

It runs in its own container on the page check's private Docker network, so the server under test reaches it
by name and no port opens on the workstation. The proxy ends TLS with the run's certificate and answers the
documented Last.fm methods with fixture names. Standard library only: the container image carries Python alone.

Usage: lastfm_stand_in.py DIRECTORY. DIRECTORY holds server.pem, server.key and session.txt; the stand-in
writes out/calls.log (one line per answered method; a track method also names the artist and title of each
track it carries, never a key, session or signature), out/tunnels.log (each tunnel request and whether
it carried a request) and out/ready once it listens. An optional loved-count.txt in DIRECTORY makes
user.getLovedTracks serve that many loved tracks named as tools/library_load.py names its generated library,
200 to a page, for tools/favourites_load.py. It is read on every request, so a run can lower it while the
stand-in serves. With it, a track.love lists that track newest first until a track.unlove, as Last.fm does.

For tools/library_load.py, an approval token APPROVAL_TOKEN-<label> signs in a Last.fm user of its own with a
session of its own, and a signed track method names that label in calls.log ("POST track.scrobble as <label> | ...").
While DIRECTORY/refuse.txt exists, scrobbles and now-playing updates answer the retryable error 16 and are recorded
as "REFUSED ...". When DIRECTORY/drop-listen.txt exists, the next scrobble is accepted but its first track is left
out of calls.log: a planted lost listen that the load check must notice.
"""

from __future__ import annotations

import http.server
import json
import os
from pathlib import Path
import re
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
# Overall play counts the history import proposes as floors for the two library tracks.
SEED_PLAYS = 42
SIMILAR_PLAYS = 7
NOW = int(time.time())
LOVED_PAGE = 200
MAX_LOVED = 20_000
APPROVAL_TOKEN = "stand-in-approval-token"
LABEL = re.compile(r"[a-z0-9]{1,16}")
# The methods a refused period answers with error 16, which the plugin retries.
REFUSABLE = ("track.scrobble", "track.updateNowPlaying")
REFUSAL = {"error": 16, "message": "There was a temporary error processing your request. Please try again"}


def label_of(token: str) -> str:
    """The account an approval token names: the part after APPROVAL_TOKEN and a dash; any other token names none."""
    label = token[len(APPROVAL_TOKEN) + 1:] if token.startswith(APPROVAL_TOKEN + "-") else ""
    return label if LABEL.fullmatch(label) else ""


def owner(values: dict[str, list[str]], session: str) -> str:
    """The label of the session a signed call carries: empty for the plain session, ? for a session never issued."""
    key = (values.get("sk") or [""])[0]
    if key == session:
        return ""
    label = key[len(session) + 1:] if key.startswith(session + "-") else ""
    return label if LABEL.fullmatch(label) else "?"


def track(title: str, artist: str = ARTIST, played: int | None = None, plays: int = 0) -> dict[str, Any]:
    value: dict[str, Any] = {"name": title, "artist": {"name": artist}, "album": {"#text": "Smoke Album"},
                             "url": "https://www.last.fm/music/" + urllib.parse.quote(artist) + "/_/" + urllib.parse.quote(title),
                             "playcount": str(plays), "mbid": ""}
    if played is not None:
        value["date"] = {"uts": str(played)}
    return value


def page(root: str, tracks: list[dict[str, Any]]) -> dict[str, Any]:
    return {root: {"@attr": {"page": "1", "totalPages": "1", "total": str(len(tracks))}, "track": tracks}}


def loved_page(count: int, values: dict[str, list[str]], loves: list[tuple[str, str, int]] | None = None) -> dict[str, Any]:
    """One page of count loved tracks, newest first, with fixed loved dates so every read sees the same pages.

    loves are the tracks the server loved, newest first; they precede the generated ones, as Last.fm lists a new love."""
    loves = loves or []
    total = len(loves) + count
    number = max(1, int((values.get("page") or ["1"])[0]))
    pages = max(1, -(-total // LOVED_PAGE))
    tracks = [track(loves[index][1], loves[index][0], played=loves[index][2]) if index < len(loves)
              else {**track(f"JellySin Load {index - len(loves):06d}", f"Artist {(index - len(loves)) // 100:04d}",
                            played=NOW - 60 * (index - len(loves))), "album": {"#text": "Load Album"}}
              for index in range((number - 1) * LOVED_PAGE, min(total, number * LOVED_PAGE))]
    return {"lovedtracks": {"@attr": {"page": str(number), "totalPages": str(pages), "total": str(total)}, "track": tracks}}


def tracks_in(values: dict[str, list[str]]) -> list[tuple[str, str]]:
    """The artist and title of each track a method carries: plain for one track, indexed for a scrobble batch."""
    keys = [""] + [f"[{index}]" for index in range(50)]
    return [((values.get("artist" + key) or [""])[0], (values.get("track" + key) or [""])[0])
            for key in keys if "artist" + key in values]


def answer(method: str, session: str, carried: list[tuple[str, str]] | None = None,
           values: dict[str, list[str]] | None = None, loved: int = 0,
           loves: list[tuple[str, str, int]] | None = None) -> dict[str, Any]:
    """What the stand-in returns for a Last.fm method: the shapes Last.fm documents, with fixture names."""
    if method == "user.getLovedTracks" and loved:
        return loved_page(loved, values or {}, loves)
    url = "https://www.last.fm/music/" + urllib.parse.quote(ARTIST)
    label = label_of(((values or {}).get("token") or [""])[0])
    answers: dict[str, Any] = {
        "auth.getSession": {"session": {"name": LASTFM_USER + ("-" + label if label else ""),
                                        "key": session + ("-" + label if label else ""), "subscriber": 0}},
        "user.getInfo": {"user": {"name": LASTFM_USER, "playcount": "1234", "artist_count": "56", "album_count": "78",
                                  "track_count": "90", "url": "https://www.last.fm/user/" + LASTFM_USER}},
        "user.getTopTracks": page("toptracks", [track(SEED, plays=SEED_PLAYS), track(SIMILAR_LOCAL, plays=SIMILAR_PLAYS)]),
        "user.getTopArtists": {"topartists": {"artist": [{"name": ARTIST, "playcount": "49", "url": url, "mbid": ""}]}},
        "user.getTopAlbums": {"topalbums": {"album": [{"name": "Smoke Album", "artist": {"name": ARTIST}, "playcount": "49",
                                                       "url": url + "/Smoke+Album", "mbid": ""}]}},
        "user.getRecentTracks": page("recenttracks", [track(SEED, played=NOW - 600), track(SIMILAR_LOCAL, played=NOW - 1200)]),
        # Loved on Last.fm only, so a favourite synchronization marks it in the library.
        "user.getLovedTracks": page("lovedtracks", [track(SIMILAR_LOCAL, played=NOW - 300)]),
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
        # One accepted entry per submitted track, as Last.fm answers a batch.
        accepted = [{"artist": {"#text": artist, "corrected": "0"}, "track": {"#text": title, "corrected": "0"},
                     "ignoredMessage": {"code": "0", "#text": ""}} for artist, title in carried or []]
        return {"scrobbles": {"@attr": {"accepted": len(accepted), "ignored": 0}, "scrobble": accepted}}
    return answers.get(method, {"error": 3, "message": "Invalid Method - No method with that name in this package"})


def loved_count(path: Path | None) -> int:
    """The loved count, read on every request so a run can drop a love while the stand-in serves."""
    if path is None or not path.exists():
        return 0
    return min(MAX_LOVED, int(path.read_text(encoding="utf-8")))


def dropped(directory: Path) -> bool:
    """True once for a planted lost listen: renaming the plant is atomic, so one scrobble alone drops a track."""
    try:
        os.replace(directory / "drop-listen.txt", directory / "drop-listen.done")
    except FileNotFoundError:
        return False
    return True


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
    directory = Path()
    loved_file: Path | None = None
    # The tracks the server loved, by artist and title, with their loved dates; listed only beside a loved count.
    loves: dict[tuple[str, str], int] = {}
    loves_lock = threading.Lock()
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
        carried = tracks_in(values) if method.startswith("track.") else []
        label = owner(values, StandIn.session) if signed and method.startswith("track.") else ""
        refused = method in REFUSABLE and (StandIn.directory / "refuse.txt").exists()
        recorded = carried[1:] if method == "track.scrobble" and not refused and dropped(StandIn.directory) else carried
        Record.write("calls.log", ("REFUSED " if refused else "") + ("POST " if signed else "GET ") + method
                     + (f" as {label}" if label else "") + "".join(f" | {artist} - {title}" for artist, title in recorded))
        if refused:
            self.send(REFUSAL)
            return
        loved = loved_count(StandIn.loved_file)
        with StandIn.loves_lock:
            if method == "track.love":
                StandIn.loves.update({key: int(time.time()) for key in carried[:1]})
            if method == "track.unlove" and carried:
                StandIn.loves.pop(carried[0], None)
            loves = sorted(((artist, title, uts) for (artist, title), uts in StandIn.loves.items()), key=lambda love: -love[2])
        self.send(answer(method, StandIn.session, carried, values, loved, loves))

    def send(self, value: dict[str, Any]) -> None:
        body = json.dumps(value).encode()
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
    StandIn.directory = directory
    StandIn.loved_file = directory / "loved-count.txt"
    StandIn.loves = {}
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
