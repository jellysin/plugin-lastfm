"""The page check's Last.fm stand-in tunnels to the Last.fm API alone and records a server that rejects its certificate."""

import http.client
import json
import socket
import ssl
import sys
import tempfile
import threading
import unittest
import urllib.parse
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
        threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.01}, daemon=True).start()
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

    def post(self, body):
        connection = self.tunnel(lastfm_stand_in.API_HOST)
        context = client_context(cafile=self.directory / "ca.pem")
        connection.connect()
        connection.sock = context.wrap_socket(connection.sock, server_hostname=lastfm_stand_in.API_HOST)
        connection.request("POST", "/2.0/", body=body, headers={"Content-Type": "application/x-www-form-urlencoded"})
        answer = json.loads(connection.getresponse().read())
        connection.close()
        return answer

    def test_a_trusting_server_receives_the_documented_answer(self):
        answer = self.post("method=auth.getSession&token=t&format=json")
        self.assertEqual({"name": lastfm_stand_in.LASTFM_USER, "key": SESSION, "subscriber": 0}, answer["session"])
        self.assertEqual(["POST auth.getSession"], self.lines("calls.log"))
        self.wait_for("tunnels.log", 2)
        self.assertEqual(["opened ws.audioscrobbler.com", "tls-ended ws.audioscrobbler.com TLSv1.3"], self.lines("tunnels.log"))

    def test_a_scrobble_batch_is_accepted_per_track_and_recorded_by_name_alone(self):
        body = urllib.parse.urlencode({"method": "track.scrobble", "artist[0]": "A & B", "track[0]": "First", "timestamp[0]": "1",
                                       "artist[1]": "C", "track[1]": "Second", "timestamp[1]": "2", "sk": SESSION, "api_sig": "f" * 32})
        answer = self.post(body)
        self.assertEqual({"accepted": 2, "ignored": 0}, answer["scrobbles"]["@attr"])
        self.assertEqual([("A & B", "First", "0"), ("C", "Second", "0")],
                         [(entry["artist"]["#text"], entry["track"]["#text"], entry["ignoredMessage"]["code"]) for entry in answer["scrobbles"]["scrobble"]])
        self.assertEqual(["POST track.scrobble | A & B - First | C - Second"], self.lines("calls.log"))

    def test_a_single_track_method_names_its_track_and_never_the_session(self):
        self.post(urllib.parse.urlencode({"method": "track.updateNowPlaying", "artist": "A", "track": "T", "sk": SESSION, "api_sig": "f" * 32}))
        self.post(urllib.parse.urlencode({"method": "user.getInfo", "user": "someone", "sk": SESSION}))
        self.assertEqual(["POST track.updateNowPlaying | A - T", "POST user.getInfo"], self.lines("calls.log"))
        self.assertNotIn(SESSION, (self.directory / "out" / "calls.log").read_text(encoding="utf-8"))

    def scrobble(self, key, title="T"):
        return self.post(urllib.parse.urlencode({"method": "track.scrobble", "artist[0]": "A", "track[0]": title, "timestamp[0]": "1",
                                                 "sk": key, "api_sig": "f" * 32}))

    def test_a_labelled_approval_token_signs_in_a_user_of_its_own(self):
        answer = self.post(f"method=auth.getSession&token={lastfm_stand_in.APPROVAL_TOKEN}-two&format=json")
        self.assertEqual({"name": lastfm_stand_in.LASTFM_USER + "-two", "key": SESSION + "-two", "subscriber": 0}, answer["session"])

    def test_a_scrobble_names_the_account_of_its_session_and_never_the_key(self):
        self.scrobble(SESSION + "-two")
        self.scrobble(SESSION + "-never-issued")
        self.scrobble("stand-in-session-other-two")
        self.assertEqual(["POST track.scrobble as two | A - T", "POST track.scrobble as ? | A - T", "POST track.scrobble as ? | A - T"],
                         self.lines("calls.log"))
        self.assertNotIn(SESSION, (self.directory / "out" / "calls.log").read_text(encoding="utf-8"))

    def test_a_refused_period_answers_the_retryable_error_and_records_the_refusal(self):
        (self.directory / "refuse.txt").write_text("error 16\n", encoding="utf-8")
        self.assertEqual(16, self.scrobble(SESSION)["error"])
        self.assertEqual(16, self.post(urllib.parse.urlencode({"method": "track.updateNowPlaying", "artist": "A", "track": "T",
                                                                "sk": SESSION, "api_sig": "f" * 32}))["error"])
        self.assertIn("session", self.post("method=auth.getSession&token=t&format=json"))
        (self.directory / "refuse.txt").unlink()
        self.assertEqual(1, self.scrobble(SESSION)["scrobbles"]["@attr"]["accepted"])
        self.assertEqual(["REFUSED POST track.scrobble | A - T", "REFUSED POST track.updateNowPlaying | A - T",
                          "POST auth.getSession", "POST track.scrobble | A - T"], self.lines("calls.log"))

    def test_a_planted_drop_loses_one_listen_of_the_next_scrobble_only(self):
        (self.directory / "drop-listen.txt").write_text("drop\n", encoding="utf-8")
        self.assertEqual(1, self.scrobble(SESSION, "First")["scrobbles"]["@attr"]["accepted"])
        self.scrobble(SESSION, "Second")
        self.assertEqual(["POST track.scrobble", "POST track.scrobble | A - Second"], self.lines("calls.log"))
        self.assertFalse((self.directory / "drop-listen.txt").exists())

    def test_a_refused_scrobble_does_not_spend_the_planted_drop(self):
        (self.directory / "drop-listen.txt").write_text("drop\n", encoding="utf-8")
        (self.directory / "refuse.txt").write_text("error 16\n", encoding="utf-8")
        self.scrobble(SESSION, "First")
        self.assertTrue((self.directory / "drop-listen.txt").exists())
        self.assertEqual(["REFUSED POST track.scrobble | A - First"], self.lines("calls.log"))

    def test_the_loved_collection_holds_a_track_the_library_has(self):
        loved = lastfm_stand_in.answer("user.getLovedTracks", SESSION)["lovedtracks"]["track"]
        self.assertEqual([lastfm_stand_in.SIMILAR_LOCAL], [entry["name"] for entry in loved])
        self.assertIn("uts", loved[0]["date"])

    def test_a_loved_count_serves_the_generated_library_in_fixed_pages(self):
        first = lastfm_stand_in.answer("user.getLovedTracks", SESSION, values={}, loved=450)["lovedtracks"]
        last = lastfm_stand_in.answer("user.getLovedTracks", SESSION, values={"page": ["3"]}, loved=450)["lovedtracks"]
        beyond = lastfm_stand_in.answer("user.getLovedTracks", SESSION, values={"page": ["4"]}, loved=450)["lovedtracks"]
        self.assertEqual({"page": "1", "totalPages": "3", "total": "450"}, first["@attr"])
        self.assertEqual((200, 50, 0), (len(first["track"]), len(last["track"]), len(beyond["track"])))
        self.assertEqual(("JellySin Load 000000", "Artist 0000", "Load Album"),
                         (first["track"][0]["name"], first["track"][0]["artist"]["name"], first["track"][0]["album"]["#text"]))
        self.assertEqual(("JellySin Load 000449", "Artist 0004"), (last["track"][-1]["name"], last["track"][-1]["artist"]["name"]))
        self.assertEqual(first, lastfm_stand_in.answer("user.getLovedTracks", SESSION, values={"page": ["1"]}, loved=450)["lovedtracks"])

    def test_a_lowered_loved_count_is_served_by_the_next_request(self):
        (self.directory / "loved-count.txt").write_text("450\n", encoding="utf-8")
        body = "method=user.getLovedTracks&user=listener&format=json"
        self.assertEqual("450", self.post(body)["lovedtracks"]["@attr"]["total"])
        (self.directory / "loved-count.txt").write_text("449\n", encoding="utf-8")
        self.assertEqual("449", self.post(body)["lovedtracks"]["@attr"]["total"])

    def test_a_love_is_listed_newest_first_until_it_is_unloved(self):
        (self.directory / "loved-count.txt").write_text("450\n", encoding="utf-8")
        loved = "method=user.getLovedTracks&user=listener&format=json"
        self.post("method=track.love&artist=Artist+9999&track=JellySin+Load+999999&format=json")
        first = self.post(loved)["lovedtracks"]
        last = self.post(loved + "&page=3")["lovedtracks"]
        self.assertEqual({"page": "1", "totalPages": "3", "total": "451"}, first["@attr"])
        self.assertEqual(("JellySin Load 999999", "Artist 9999"), (first["track"][0]["name"], first["track"][0]["artist"]["name"]))
        self.assertIn("uts", first["track"][0]["date"])
        self.assertEqual(("JellySin Load 000000", "JellySin Load 000449"), (first["track"][1]["name"], last["track"][-1]["name"]))
        self.post("method=track.unlove&artist=Artist+9999&track=JellySin+Load+999999&format=json")
        unloved = self.post(loved)["lovedtracks"]
        self.assertEqual(("450", "JellySin Load 000000"), (unloved["@attr"]["total"], unloved["track"][0]["name"]))

    def test_without_a_loved_count_a_love_changes_no_listing(self):
        self.post("method=track.love&artist=Artist+9999&track=JellySin+Load+999999&format=json")
        loved = self.post("method=user.getLovedTracks&user=listener&format=json")["lovedtracks"]["track"]
        self.assertEqual([lastfm_stand_in.SIMILAR_LOCAL], [entry["name"] for entry in loved])

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
