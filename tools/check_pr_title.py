"""Validate Conventional Commit PR titles, including explicitly dispatched bot CI."""

import json
import os
from pathlib import Path
import re
import subprocess
import sys
from urllib.parse import urlencode


TITLE = re.compile(
    r"(?:feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert)"
    r"(?:\((?P<scope>[^()\r\n]+)\))?!?: (?P<description>[^\r\n]+)\Z"
)
TITLE_ERROR = "PR title must use Conventional Commits: type(scope): description; scope and ! are optional."


def validate_title(title):
    if not isinstance(title, str) or any(ord(character) < 32 or ord(character) == 127 for character in title):
        raise ValueError(TITLE_ERROR)
    match = TITLE.fullmatch(title)
    if match is None or not match["description"].strip() or (match["scope"] is not None and not match["scope"].strip()):
        raise ValueError(TITLE_ERROR)


def is_automation_branch(branch):
    return branch == "automation/plugin-catalog" or branch.startswith("release-please--")


def fetch_pull_requests(repository, branch):
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("A valid GitHub repository is required to inspect bot PR metadata.")
    owner = repository.split("/", 1)[0]
    query = urlencode({"state": "open", "head": f"{owner}:{branch}", "per_page": 100})
    result = subprocess.run(
        ["gh", "api", f"repos/{repository}/pulls?{query}", "--method", "GET"],
        check=True, capture_output=True, text=True, encoding="utf-8", timeout=30,
    )
    pulls = json.loads(result.stdout)
    if not isinstance(pulls, list) or len(pulls) >= 100:
        raise ValueError("Unable to identify an unambiguous open automation PR.")
    return pulls


def title_for_head(pulls, repository, branch, expected_sha):
    if not isinstance(expected_sha, str) or not re.fullmatch(r"[0-9a-fA-F]{40}", expected_sha):
        raise ValueError("Bot PR validation requires its exact expected head commit.")
    matches = []
    for pull in pulls:
        head = pull.get("head", {})
        head_repository = head.get("repo") or {}
        if (pull.get("state") == "open"
                and head_repository.get("full_name", "").casefold() == repository.casefold()
                and head.get("ref") == branch and head.get("sha") == expected_sha):
            matches.append(pull)
    if len(matches) != 1:
        raise ValueError("Expected exactly one open, same-repository automation PR at the dispatched commit.")
    return matches[0].get("title")


def check_event(event_name, event, repository="", branch="", commit=""):
    if event_name == "pull_request":
        validate_title(event.get("pull_request", {}).get("title"))
        return True
    if event_name == "workflow_dispatch" and is_automation_branch(branch):
        expected_sha = event.get("inputs", {}).get("expected_sha") or commit
        pulls = fetch_pull_requests(repository, branch)
        validate_title(title_for_head(pulls, repository, branch, expected_sha))
        return True
    return False


def main():
    try:
        event_name = os.environ.get("GITHUB_EVENT_NAME", "")
        branch = os.environ.get("GITHUB_REF_NAME", "")
        if event_name != "pull_request" and not (event_name == "workflow_dispatch" and is_automation_branch(branch)):
            print("No PR title applies to this event.")
            return 0
        event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text(encoding="utf-8"))
        check_event(event_name, event, os.environ.get("GITHUB_REPOSITORY", ""), branch, os.environ.get("GITHUB_SHA", ""))
        print("PR title follows Conventional Commits.")
        return 0
    except ValueError as error:
        print(f"::error::{error}", file=sys.stderr)
    except (KeyError, OSError, subprocess.SubprocessError):
        print("::error::Unable to read the PR metadata required for title validation.", file=sys.stderr)
    return 1


if __name__ == "__main__":
    sys.exit(main())
