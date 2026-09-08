"""Regression tests for ordinary and bot-generated PR title gates."""

import copy
import io
import json
import unittest
from unittest.mock import patch

import check_pr_title


REPOSITORY = "lusoris/jellyfin-plugin-lastfm"
BRANCH = "release-please--branches--main"
SHA = "a" * 40


def pull(title="chore: release main", branch=BRANCH, sha=SHA, repository=REPOSITORY):
    return {"number": 1, "state": "open", "title": title, "head": {"ref": branch, "sha": sha, "repo": {"full_name": repository}}}


class TitleTests(unittest.TestCase):
    def test_supported_types_scopes_and_breaking_markers(self):
        for kind in ("feat", "fix", "docs", "style", "refactor", "perf", "test", "build", "ci", "chore", "revert"):
            for suffix in (": describe change", "(playback): describe change", "!: breaking change", "(api)!: breaking change"):
                with self.subTest(title=kind + suffix):
                    check_pr_title.validate_title(kind + suffix)

    def test_invalid_titles_fail_closed(self):
        for title in (None, "", "Fix playback", "unknown: change", "fix:", "fix: ", "fix:   ", "fix(): change", "fix(  ): change", "fix: change\nchore: another", "fix: change\r", "fix: change\x00", " fix: change", "fix: \t"):
            with self.subTest(title=title), self.assertRaises(ValueError):
                check_pr_title.validate_title(title)

    def test_description_can_contain_unicode_and_literal_shell_syntax(self):
        check_pr_title.validate_title("fix(ui): preserve Björk's title and literal $(example) text")


class EventTests(unittest.TestCase):
    def test_pull_request_event_reads_title_without_network(self):
        with patch.object(check_pr_title, "fetch_pull_requests") as fetch:
            self.assertTrue(check_pr_title.check_event("pull_request", {"pull_request": {"title": "fix: correct scrobbling"}}))
        fetch.assert_not_called()

    def test_missing_or_invalid_pull_request_title_is_rejected(self):
        for event in ({}, {"pull_request": {}}, {"pull_request": {"title": "bad title"}}):
            with self.subTest(event=event), self.assertRaises(ValueError):
                check_pr_title.check_event("pull_request", event)

    def test_push_tag_and_manual_release_events_skip_without_network(self):
        for event_name, branch in (("push", "main"), ("workflow_call", "v12.0.0"), ("workflow_dispatch", "main"), ("workflow_dispatch", "release/10.11"), ("workflow_dispatch", "v12.0.0")):
            with self.subTest(event=event_name, branch=branch), patch.object(check_pr_title, "fetch_pull_requests") as fetch:
                self.assertFalse(check_pr_title.check_event(event_name, {}, REPOSITORY, branch, SHA))
                fetch.assert_not_called()

    def test_dispatch_checks_open_pr_at_exact_input_sha(self):
        with patch.object(check_pr_title, "fetch_pull_requests", return_value=[pull()]) as fetch:
            self.assertTrue(check_pr_title.check_event("workflow_dispatch", {"inputs": {"expected_sha": SHA}}, REPOSITORY, BRANCH, "b" * 40))
        fetch.assert_called_once_with(REPOSITORY, BRANCH)

    def test_manual_bot_dispatch_uses_workflow_commit_when_no_explicit_sha(self):
        with patch.object(check_pr_title, "fetch_pull_requests", return_value=[pull(branch="automation/plugin-catalog")]):
            self.assertTrue(check_pr_title.check_event("workflow_dispatch", {}, REPOSITORY, "automation/plugin-catalog", SHA))

    def test_head_mismatch_fork_closed_and_ambiguous_pr_are_rejected(self):
        closed = pull()
        closed["state"] = "closed"
        missing_repository = pull()
        missing_repository["head"]["repo"] = None
        for pulls in ([], [pull(sha="b" * 40)], [pull(branch="other")], [pull(repository="attacker/fork")], [closed], [missing_repository], [pull(), copy.deepcopy(pull())]):
            with self.subTest(pulls=pulls), self.assertRaises(ValueError):
                check_pr_title.title_for_head(pulls, REPOSITORY, BRANCH, SHA)

    def test_invalid_bot_title_and_missing_sha_are_rejected(self):
        with patch.object(check_pr_title, "fetch_pull_requests", return_value=[pull(title="bad title")]):
            with self.assertRaises(ValueError):
                check_pr_title.check_event("workflow_dispatch", {}, REPOSITORY, BRANCH, SHA)
            with self.assertRaises(ValueError):
                check_pr_title.check_event("workflow_dispatch", {}, REPOSITORY, BRANCH)

    def test_gh_request_is_read_only_bounded_and_branch_is_query_encoded(self):
        with patch.object(check_pr_title.subprocess, "run") as run:
            run.return_value.stdout = json.dumps([pull()])
            self.assertEqual([pull()], check_pr_title.fetch_pull_requests(REPOSITORY, "release-please--branch&other=value"))
        args, kwargs = run.call_args
        self.assertEqual(["gh", "api"], args[0][:2])
        self.assertEqual(["--method", "GET"], args[0][-2:])
        self.assertIn("%26other%3Dvalue", args[0][2])
        self.assertEqual(30, kwargs["timeout"])
        self.assertNotIn("shell", kwargs)

    def test_main_reads_event_file_and_does_not_echo_untrusted_title(self):
        title = "invalid\n::error::injected workflow command"
        env = {"GITHUB_EVENT_NAME": "pull_request", "GITHUB_EVENT_PATH": "event.json"}
        with patch.dict(check_pr_title.os.environ, env, clear=True), patch.object(check_pr_title.Path, "read_text", return_value=json.dumps({"pull_request": {"title": title}})), patch("sys.stderr", new_callable=io.StringIO) as output:
            self.assertEqual(1, check_pr_title.main())
            self.assertNotIn("injected", output.getvalue())


if __name__ == "__main__":
    unittest.main()
