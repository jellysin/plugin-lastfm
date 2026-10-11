"""A server run names one pinned image, proves which release answered and removes what it started."""

import argparse
import contextlib
import io
import unittest
from unittest.mock import patch

from tools import smoke
from tools.smoke import DEFAULT_SERVER, IMAGES, SmokeFailure, add_server_options, chosen_image, remove_container, require_release

DIGEST = "@sha256:" + "a" * 64


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
    def test_both_pinned_servers_carry_a_release_and_a_digest(self):
        self.assertEqual(["floor", "newest"], sorted(IMAGES))
        for image in IMAGES.values():
            self.assertIsNotNone(smoke.IMAGE_TAG.fullmatch(image), image)

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


if __name__ == "__main__":
    unittest.main()
