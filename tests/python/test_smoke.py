"""A server run names one pinned image, proves which release answered and removes what it started."""

import argparse
import contextlib
import copy
import http.server
import io
import json
import re
import threading
import unittest
from pathlib import Path
from unittest.mock import patch

from tools import smoke
from tools.smoke import DEFAULT_SERVER, IMAGES, SmokeFailure, add_server_options, chosen_image, remove_container, require_release

DIGEST = "@sha256:" + "a" * 64
UPDATES = json.loads((Path(__file__).resolve().parents[2] / "renovate.json").read_text(encoding="utf-8"))
RELEASE = re.compile(r"(\d+)\.(\d+)")


def held(rules, name):
    """Tell whether exactly one update rule keeps the server of that name at the release it is named after."""
    patterns = [rule.get("allowedVersions", "") for rule in rules if rule.get("matchDepTypes") == [name]]
    if len(patterns) != 1 or len(patterns[0]) < 3 or not patterns[0].startswith("/") or not patterns[0].endswith("/"):
        return False
    allowed = re.compile(patterns[0][1:-1])
    major, minor = (int(part) for part in RELEASE.fullmatch(name).groups())
    others = (f"{major}.{minor + 1}", f"{major}.{minor}0.0", f"{major + 1}.{minor}.0")
    return all(allowed.search(version) for version in (name, name + ".3")) and not any(allowed.search(version) for version in others)


def pin_drift(images, updates):
    """Return every pinned server the update bot does not read or could move off the release it is named after."""
    (manager,) = [entry for entry in updates["customManagers"] if entry["managerFilePatterns"] == ["/^tools/smoke\\.py$/"]]
    read = re.compile(manager["matchStrings"][0].replace("(?<", "(?P<"))
    wrong = []
    for name, image in images.items():
        entry = read.fullmatch(json.dumps(name) + ": " + json.dumps(image))
        if entry is None or entry["depType"] != name:
            wrong.append(f"{name}: not read by the update bot")
        if not RELEASE.fullmatch(name):
            continue
        if smoke.IMAGE_TAG.fullmatch(image).group(1) != name:
            wrong.append(f"{name}: the image names another release")
        if not held(updates["packageRules"], name):
            wrong.append(f"{name}: no single update rule holds it at its release")
    return wrong


def changed(rule=None, manager=None):
    """Return the update configuration with the 12.1 rule or the server pattern replaced."""
    updates = copy.deepcopy(UPDATES)
    if manager:
        updates["customManagers"][1]["matchStrings"] = [updates["customManagers"][1]["matchStrings"][0].replace(*manager)]
    if rule is not None:
        updates["packageRules"] = [entry for entry in updates["packageRules"] if entry.get("matchDepTypes") != ["12.1"]] + rule
    return updates


def chosen(*arguments):
    parser = argparse.ArgumentParser()
    add_server_options(parser)
    with contextlib.redirect_stderr(io.StringIO()):
        return chosen_image(parser, parser.parse_args(arguments))


def removed(outcomes):
    """Run the removal against planted Docker results; return (removed, attempts, pauses)."""
    planted = iter(outcomes)

    def fake_docker(*arguments, **_):
        if next(planted):
            raise SmokeFailure("Docker rm failed (exit 1).")
        return ""

    with patch.object(smoke, "docker", side_effect=fake_docker) as docker, patch.object(smoke.time, "sleep") as sleep:
        return remove_container("jellysin-smoke-fixture"), docker.call_count, sleep.call_count


class ServerChoiceTests(unittest.TestCase):
    def test_every_pinned_server_carries_a_release_and_a_digest(self):
        self.assertEqual(["12.1", "floor", "newest"], sorted(IMAGES))
        for image in IMAGES.values():
            self.assertIsNotNone(smoke.IMAGE_TAG.fullmatch(image), image)

    def test_update_bot_reads_every_pin_and_holds_a_release_named_one(self):
        self.assertEqual([], pin_drift(IMAGES, UPDATES))

    def test_pin_the_update_bot_could_lose_or_move_is_named(self):
        narrow = {"matchDepTypes": ["12.1"], "allowedVersions": "/^12\\.1(?:\\.|$)/"}
        unheld = "12.1: no single update rule holds it at its release"
        planted = [
            ("12.1: the image names another release", {"12.1": "jellyfin/jellyfin:12.2" + DIGEST}, UPDATES),
            (unheld, IMAGES, changed(rule=[])),
            (unheld, IMAGES, changed(rule=[narrow, narrow])),
            (unheld, IMAGES, changed(rule=[narrow | {"allowedVersions": "/^12(?:\\.|$)/"}])),
            (unheld, IMAGES, changed(rule=[narrow | {"allowedVersions": "/^12\\.1/"}])),
            (unheld, IMAGES, changed(rule=[narrow | {"allowedVersions": "12.1"}])),
            ("12.1: not read by the update bot", IMAGES, changed(manager=("|[0-9]+\\.[0-9]+", ""))),
            ("spare: not read by the update bot", {"spare": IMAGES["newest"]}, UPDATES),
        ]
        for number, (named, images, updates) in enumerate(planted):
            with self.subTest(number=number, named=named):
                self.assertEqual([named], pin_drift(images, updates))

    def test_named_server_selects_its_pinned_image(self):
        self.assertEqual(IMAGES["floor"], chosen("--server", "floor"))
        self.assertEqual(IMAGES[DEFAULT_SERVER], chosen())
        self.assertEqual("jellyfin/jellyfin:12.1" + DIGEST, chosen("--image", "jellyfin/jellyfin:12.1" + DIGEST))

    def test_refuses_an_unpinned_or_doubly_named_server(self):
        refused = {
            "image without a digest": ["--image", "jellyfin/jellyfin:12.2"],
            "server and image together": ["--server", "floor", "--image", "jellyfin/jellyfin:12.2" + DIGEST],
            "unknown server": ["--server", "latest"],
        }
        for label, arguments in refused.items():
            with self.subTest(label), self.assertRaises(SystemExit) as stopped:
                chosen(*arguments)
            self.assertEqual(2, stopped.exception.code)


class ReleaseTests(unittest.TestCase):
    def test_accepts_the_release_the_tag_names(self):
        for tag, version in (("12.0", "12.0.0"), ("12.0", "12.0.4"), ("12.2.1", "12.2.1"), ("12.2.0", "12.2.3")):
            require_release(f"jellyfin/jellyfin:{tag}{DIGEST}", version)
        require_release("registry.invalid/other:nightly" + DIGEST, "12.9.0")

    def test_refuses_another_release_than_the_tag_names(self):
        for tag, version in (("12.0", "12.2.0"), ("12.2", "12.0.0"), ("12.0", "12.01.0"), ("12.0", ""), ("12.2", "13.2.0")):
            with self.subTest(tag=tag, version=version), self.assertRaisesRegex(SmokeFailure, "not the release its image tag names"):
                require_release(f"jellyfin/jellyfin:{tag}{DIGEST}", version)


class RemovalTests(unittest.TestCase):
    def test_first_removal_needs_no_pause(self):
        self.assertEqual((True, 1, 0), removed([False]))

    def test_refused_removal_is_repeated_after_a_pause(self):
        self.assertEqual((True, 3, 2), removed([True, True, False]))

    def test_removal_that_keeps_failing_is_reported_after_a_bounded_number_of_attempts(self):
        attempts = smoke.REMOVE_ATTEMPTS
        self.assertEqual((False, attempts, attempts - 1), removed([True] * (attempts + 5)))


class ConflictMarkTests(unittest.TestCase):
    MARKED = json.dumps({"type": smoke.NOT_CONNECTED, "title": "Connect Last.fm first.", "status": 409}).encode()
    PLAIN = json.dumps({"type": "https://tools.ietf.org/html/rfc9110#section-15.5.10", "status": 409}).encode()

    def test_accepts_a_marked_view_beside_a_conflict_of_another_kind(self):
        smoke.check_conflict_marks(self.MARKED, self.PLAIN)
        smoke.check_conflict_marks(json.dumps({"Type": smoke.NOT_CONNECTED}).encode(), b"")

    def test_refuses_a_view_without_the_mark(self):
        for label, view in {"another type": self.PLAIN, "no type": b'{"status": 409}', "no answer": b"", "not an object": b"[]",
                            "the mark as title only": json.dumps({"title": smoke.NOT_CONNECTED}).encode()}.items():
            with self.subTest(label), self.assertRaisesRegex(SmokeFailure, "must carry"):
                smoke.check_conflict_marks(view, self.PLAIN)

    def test_refuses_the_mark_on_a_conflict_of_another_kind(self):
        with self.assertRaisesRegex(SmokeFailure, "must not carry"):
            smoke.check_conflict_marks(self.MARKED, self.MARKED)


REFUSED = {"Location": "/jellyfin/JellySin/Lastfm/?connection=failed", "Cache-Control": "no-store", "Referrer-Policy": "no-referrer"}


def returned(status, headers):
    """Run the return-address check against a local server that gives the planted answer; return what it was asked."""
    asked = []

    class Planted(http.server.BaseHTTPRequestHandler):
        def log_message(self, _format, *arguments):
            pass

        def do_GET(self):
            asked.append((self.path, self.headers.get("Authorization"), self.headers.get("Cookie")))
            self.send_response(status)
            for name, value in headers.items():
                self.send_header(name, value)
            self.send_header("Content-Length", "0")
            self.end_headers()

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Planted)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        smoke.check_return_address(smoke.Client(f"http://127.0.0.1:{server.server_address[1]}", "/jellyfin", token="a" * 32))
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)
    return asked


def began(back, cookie, published="", configured=True, asked=None):
    """Run the sign-in check against a local server that names the planted return and cookie; return what it was asked."""
    asked = [] if asked is None else asked

    class Planted(http.server.BaseHTTPRequestHandler):
        def log_message(self, _format, *arguments):
            pass

        def answer(self):
            asked.append((self.command, self.path))
            self.rfile.read(int(self.headers.get("Content-Length", 0)))
            start = self.command == "POST"
            named = back.replace("{origin}", f"http://127.0.0.1:{self.server.server_address[1]}")
            body = json.dumps({"AuthorizationUrl": "https://www.last.fm/api/auth/?api_key=planted&cb=" + smoke.urllib.parse.quote(named, safe="")}).encode() if start else b""
            self.send_response(200 if start else 204)
            if start:
                self.send_header("Set-Cookie", cookie)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        do_POST = do_PUT = do_DELETE = answer

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Planted)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    origin = f"http://127.0.0.1:{server.server_address[1]}"
    try:
        client = smoke.Client(origin, "/jellyfin", token="a" * 32)
        smoke.check_sign_in_address(client, client, published, configured)
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)
    return asked


def wrote(accepts, published=""):
    """Run the page-origin check against a local server that accepts a write by the planted rule; return what it was asked."""
    asked = []

    class Planted(http.server.BaseHTTPRequestHandler):
        def log_message(self, _format, *arguments):
            pass

        def do_PUT(self):
            self.rfile.read(int(self.headers.get("Content-Length", 0)))
            own = f"http://127.0.0.1:{self.server.server_address[1]}"
            origin, site = self.headers.get("Origin"), self.headers.get("Sec-Fetch-Site")
            asked.append((self.path, "{own}" if origin == own else origin, site))
            body = b"{}" if accepts(origin, site, own) else b""
            self.send_response(200 if body else 403)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Planted)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        smoke.check_page_origin(smoke.Client(f"http://127.0.0.1:{server.server_address[1]}", "/jellyfin", token="a" * 32), published)
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=5)
    return asked


def own_pages(published):
    """The rule of a server that takes its published address and the address of the request for its own pages."""
    return lambda origin, site, own: site == "same-origin" and origin in (own, published)


class PageOriginTests(unittest.TestCase):
    FAVOURITES = "/jellyfin/JellySin/Lastfm/Me/Favourites"

    def test_asks_from_each_own_address_and_from_three_other_places(self):
        self.assertEqual([(self.FAVOURITES, "{own}", "same-origin"), (self.FAVOURITES, PUBLISHED, "same-origin"),
                          (self.FAVOURITES, "https://foreign.invalid", "same-origin"), (self.FAVOURITES, "http://media.example", "same-origin"),
                          (self.FAVOURITES, PUBLISHED, "cross-site")], wrote(own_pages(PUBLISHED), PUBLISHED))
        asked = wrote(own_pages(None))
        self.assertEqual(["{own}", "https://foreign.invalid", "{own}"], [origin for _, origin, _ in asked if not origin.startswith("https://127.")])
        self.assertRegex(asked[2][1], r"^https://127\.0\.0\.1:\d+$")
        self.assertEqual(4, len(asked))

    def test_refuses_a_server_that_compares_the_scheme_of_the_page_with_the_scheme_it_sees(self):
        with self.assertRaisesRegex(SmokeFailure, "published server address was refused"):
            wrote(own_pages(None), PUBLISHED)

    def test_refuses_a_server_that_turns_its_own_address_away(self):
        with self.assertRaisesRegex(SmokeFailure, "request's own address was refused"):
            wrote(lambda origin, site, own: site == "same-origin" and origin == PUBLISHED, PUBLISHED)

    def test_refuses_a_server_that_accepts_a_page_of_another_server(self):
        with self.assertRaisesRegex(SmokeFailure, "page of another server was accepted"):
            wrote(lambda origin, site, own: site == "same-origin", PUBLISHED)

    def test_refuses_a_server_that_reads_only_the_host_of_the_page(self):
        with self.assertRaisesRegex(SmokeFailure, "under another scheme was accepted"):
            wrote(lambda origin, site, own: site == "same-origin" and origin.split("://")[1] in (own.split("://")[1], "media.example"), PUBLISHED)
        with self.assertRaisesRegex(SmokeFailure, "under another scheme was accepted"):
            wrote(lambda origin, site, own: site == "same-origin" and origin.split("://")[1] == own.split("://")[1])

    def test_refuses_a_server_that_ignores_what_the_browser_says_about_the_sender(self):
        with self.assertRaisesRegex(SmokeFailure, "marks as sent by another site was accepted"):
            wrote(lambda origin, site, own: origin in (own, PUBLISHED), PUBLISHED)


OWN = "{origin}/jellyfin/JellySin/Lastfm/Callback?attempt=" + "0" * 32
PUBLISHED = "https://media.example"
PLAIN_COOKIE = "JellySinLastfmAttempt=planted; path=/jellyfin/JellySin/Lastfm/Callback; httponly; samesite=lax"
SECURE_COOKIE = "__Host-JellySinLastfmAttempt=planted; path=/; secure; httponly; samesite=lax"


class SignInAddressTests(unittest.TestCase):
    def test_accepts_the_request_address_with_a_plain_cookie_when_none_is_published(self):
        self.assertEqual([("POST", "/jellyfin/JellySin/Lastfm/Me/Connection/Begin")], began(OWN, PLAIN_COOKIE))

    def test_accepts_the_published_address_with_a_cookie_for_encrypted_pages_and_leaves_a_configured_application_alone(self):
        self.assertEqual([("POST", "/jellyfin/JellySin/Lastfm/Me/Connection/Begin")],
                         began(PUBLISHED + "/jellyfin/JellySin/Lastfm/Callback?attempt=" + "0" * 32, SECURE_COOKIE, PUBLISHED))

    def test_plants_an_application_for_a_build_without_one_and_removes_it_even_after_a_refusal(self):
        application = "/jellyfin/JellySin/Lastfm/Admin/Application"
        expected = [("PUT", application), ("POST", "/jellyfin/JellySin/Lastfm/Me/Connection/Begin"), ("DELETE", application)]
        self.assertEqual(expected, began("http://media.example/jellyfin/JellySin/Lastfm/Callback?attempt=1", PLAIN_COOKIE, "http://media.example", configured=False))
        asked = []
        with self.assertRaisesRegex(SmokeFailure, "published server address"):
            began("http://other.example/jellyfin/JellySin/Lastfm/Callback?attempt=1", PLAIN_COOKIE, "http://media.example", configured=False, asked=asked)
        self.assertEqual(expected, asked)

    def test_refuses_a_return_under_the_request_address_another_host_a_lost_base_path_or_a_plain_scheme(self):
        for back in ("http://127.0.0.1:8096/jellyfin/JellySin/Lastfm/Callback?attempt=1", "https://attacker.example/jellyfin/JellySin/Lastfm/Callback?attempt=1",
                     "https://media.example/JellySin/Lastfm/Callback?attempt=1", "http://media.example/jellyfin/JellySin/Lastfm/Callback?attempt=1",
                     "https://media.example/jellyfin/JellySin/Lastfm/Callback/more?attempt=1", ""):
            with self.subTest(back=back), self.assertRaisesRegex(SmokeFailure, "published server address"):
                began(back, SECURE_COOKIE, PUBLISHED)

    def test_refuses_a_published_address_named_when_none_is_published(self):
        with self.assertRaisesRegex(SmokeFailure, "request's own address"):
            began(PUBLISHED + "/jellyfin/JellySin/Lastfm/Callback?attempt=1", PLAIN_COOKIE)
        with self.assertRaisesRegex(SmokeFailure, "cookie of the sign-in"):
            began(OWN, SECURE_COOKIE)

    def test_refuses_a_cookie_that_does_not_follow_the_scheme_of_the_return(self):
        back = PUBLISHED + "/jellyfin/JellySin/Lastfm/Callback?attempt=1"
        for cookie in (PLAIN_COOKIE, "__Host-JellySinLastfmAttempt=planted; path=/; httponly", "JellySinLastfmAttempt=planted; path=/; secure; httponly", ""):
            with self.subTest(cookie=cookie), self.assertRaisesRegex(SmokeFailure, "cookie of the sign-in"):
                began(back, cookie, PUBLISHED)
        with self.assertRaisesRegex(SmokeFailure, "cookie of the sign-in"):
            began("http://media.example/jellyfin/JellySin/Lastfm/Callback?attempt=1", SECURE_COOKIE, "http://media.example")

    def test_no_failure_text_names_the_application_key(self):
        try:
            began("https://attacker.example/x", "", PUBLISHED)
        except SmokeFailure as error:
            self.assertNotIn("planted", str(error))
        else:
            self.fail("The planted return was accepted.")


class ReturnAddressTests(unittest.TestCase):
    def test_accepts_a_refusal_that_leads_back_to_the_page_and_asks_without_a_sign_in(self):
        self.assertEqual([("/jellyfin/JellySin/Lastfm/Callback?attempt=" + "0" * 32 + "&token=smoke-return-token", None, None)], returned(302, REFUSED))

    def test_refuses_a_return_address_that_needs_a_sign_in_or_shows_a_page(self):
        for status in (200, 401, 404):
            with self.subTest(status=status), self.assertRaisesRegex(SmokeFailure, "without a Jellyfin sign-in"):
                returned(status, REFUSED)

    def test_refuses_a_token_passed_on_a_lost_base_path_or_a_missing_refusal(self):
        for location in ("/jellyfin/JellySin/Lastfm/?connection=failed&token=smoke-return-token", "/JellySin/Lastfm/?connection=failed",
                         "/jellyfin/JellySin/Lastfm/", "https://attacker.example/JellySin/Lastfm/?connection=failed"):
            with self.subTest(location=location), self.assertRaisesRegex(SmokeFailure, "marked as refused and without its token"):
                returned(302, REFUSED | {"Location": location})

    def test_refuses_an_answer_that_may_be_cached_or_names_its_address_to_the_next_page(self):
        for name, value, expected in (("Cache-Control", "private", "must not be cached"), ("Referrer-Policy", "origin", "referrer leakage")):
            with self.subTest(header=name), self.assertRaisesRegex(SmokeFailure, expected):
                returned(302, REFUSED | {name: value})


if __name__ == "__main__":
    unittest.main()
