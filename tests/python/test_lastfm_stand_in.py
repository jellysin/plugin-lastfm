"""The page check's Last.fm stand-in tunnels to the Last.fm API alone and records a server that rejects its certificate."""

import http.client
import json
import socket
import ssl
import sys
import tempfile
import threading
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tests" / "integration"))
import lastfm_stand_in  # noqa: E402

SESSION = "stand-in-session-test"


def client_context(cafile=None):
    context = ssl.create_default_context(cafile=cafile)
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    return context


class StandInTests(unittest.TestCase):
    def setUp(self):
        scratch = tempfile.TemporaryDirectory()
        self.addCleanup(scratch.cleanup)
        self.directory = Path(scratch.name)
        lastfm_stand_in.make_certificates(self.directory)
        (self.directory / "session.txt").write_text(SESSION + "\n", encoding="utf-8")
        (self.directory / "out").mkdir()
        server = lastfm_stand_in.bind(self.directory, ("127.0.0.1", 0))
        self.port = server.server_address[1]
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)

    def lines(self, name):
        path = self.directory / "out" / name
        return path.read_text(encoding="utf-8").splitlines() if path.exists() else []

    def wait_for(self, name, count):
        for _ in range(200):
            if len(self.lines(name)) >= count:
                return
            threading.Event().wait(0.02)
        self.fail(f"{name} did not reach {count} lines: {self.lines(name)}")

    def tunnel(self, host):
        connection = http.client.HTTPConnection("127.0.0.1", self.port, timeout=10)
        connection.set_tunnel(host, 443)
        self.addCleanup(connection.close)
        return connection

    def test_a_trusting_server_receives_the_documented_answer(self):
        connection = self.tunnel(lastfm_stand_in.API_HOST)
        context = client_context(cafile=self.directory / "ca.pem")
        connection.connect()
        connection.sock = context.wrap_socket(connection.sock, server_hostname=lastfm_stand_in.API_HOST)
        connection.request("POST", "/2.0/", body="method=auth.getSession&token=t&format=json",
                           headers={"Content-Type": "application/x-www-form-urlencoded"})
        answer = json.loads(connection.getresponse().read())
        connection.close()
        self.assertEqual({"name": lastfm_stand_in.LASTFM_USER, "key": SESSION, "subscriber": 0}, answer["session"])
        self.assertEqual(["POST auth.getSession"], self.lines("calls.log"))
        self.wait_for("tunnels.log", 2)
        self.assertEqual(["opened ws.audioscrobbler.com", "tls-ended ws.audioscrobbler.com TLSv1.3"], self.lines("tunnels.log"))

    def test_a_server_that_rejects_the_certificate_is_recorded(self):
        connection = self.tunnel(lastfm_stand_in.API_HOST)
        connection.connect()
        with self.assertRaises(ssl.SSLCertVerificationError):
            client_context().wrap_socket(connection.sock, server_hostname=lastfm_stand_in.API_HOST)
        self.wait_for("tunnels.log", 2)
        self.assertEqual([], self.lines("calls.log"))
        # Depending on the client the stand-in sees the rejection during its handshake or as a tunnel without a request.
        self.assertTrue(self.lines("tunnels.log")[1].startswith(("tls-failed ws.audioscrobbler.com ", "no-request ws.audioscrobbler.com ")),
                        self.lines("tunnels.log"))

    def test_any_other_host_is_refused(self):
        connection = self.tunnel("musicbrainz.org")
        with self.assertRaises(OSError):
            connection.connect()
        self.assertEqual(["refused musicbrainz.org"], self.lines("tunnels.log"))

    def test_plain_http_is_never_forwarded(self):
        with socket.create_connection(("127.0.0.1", self.port), timeout=10) as raw:
            raw.sendall(b"GET http://ws.audioscrobbler.com/2.0/ HTTP/1.1\r\nHost: ws.audioscrobbler.com\r\n\r\n")
            self.assertIn(b" 403 ", raw.recv(256))
        self.assertEqual(["refused ws.audioscrobbler.com"], self.lines("tunnels.log"))


if __name__ == "__main__":
    unittest.main()
