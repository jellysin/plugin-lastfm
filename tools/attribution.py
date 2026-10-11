"""Refuse commit messages and pull request texts that credit a model or coding tool for a change."""

import argparse
import json
import re
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path

MAX_EVENT_BYTES = 4 * 1024 * 1024
HISTORY_TIMEOUT_SECONDS = 120
# A trailer such as Co-authored-by, Assisted-by or Generated-by, with the party it names.
TRAILER = re.compile(r"[a-z][a-z-]*-by:\s*(?P<name>[^<]*)(?P<address><[^>]*>)?.*", re.IGNORECASE)
# A footer line that opens with the credit: "Generated with [Tool](address)".
FOOTER = re.compile(r"[\W_]*(generated|written|created|authored|made|built)\s+(with|by|using)\s+\W*(?P<name>.*)", re.IGNORECASE)
# Names no person carries.
PRODUCT = re.compile(r"\b(claude code|codex|copilot|chatgpt|gpt-?\d|openai|anthropic|gemini|antigravity|aider|windsurf|codeium|cursor ?agent)\b", re.IGNORECASE)
# Names a person can carry too: a tool only when alone or followed by a model or product word.
SHARED = re.compile(r"(claude|devin|cursor)(\s+(opus|sonnet|haiku|fable|ai|agent|code|\d)\b.*)?", re.IGNORECASE)
ADDRESS = re.compile(r"<[^>@]*@(anthropic\.com|[^>]*\.anthropic\.com)>", re.IGNORECASE)


class AttributionError(ValueError):
    """The history or the event could not be read completely, so nothing is concluded from it."""


@dataclass(frozen=True)
class Finding:
    source: str
    number: int
    line: str


def names_tool(name):
    cleaned = re.sub(r"[\[\]()*_`]", " ", name).strip()
    return bool(PRODUCT.search(cleaned) or SHARED.fullmatch(cleaned))


def credits_tool(line):
    """True when one line is a trailer or a footer that names a model or coding tool."""
    text = line.strip()
    trailer = TRAILER.fullmatch(text)
    if trailer:
        return names_tool(trailer["name"]) or bool(ADDRESS.fullmatch(trailer["address"] or ""))
    footer = FOOTER.fullmatch(text)
    if not footer:
        return False
    # The credited name ends at the link target, a sentence end or a dash.
    named = re.split(r"\]|\(|[.,;:!?](\s|$)|\s[-–—]\s", footer["name"], maxsplit=1)[0]
    return names_tool(named)


def scan(source, text):
    return [Finding(source, number, line.strip()) for number, line in enumerate(text.splitlines(), 1) if credits_tool(line)]


def git(repository, *arguments):
    try:
        done = subprocess.run(["git", "-C", str(repository), *arguments], capture_output=True, check=False, timeout=HISTORY_TIMEOUT_SECONDS)
    except (OSError, subprocess.TimeoutExpired) as error:
        raise AttributionError(f"git {arguments[0]} did not complete: {error}") from error
    if done.returncode != 0:
        raise AttributionError(f"git {arguments[0]} failed: {done.stderr.decode(errors='replace').strip()}")
    return done.stdout.decode(errors="replace")


def history(repository, revision):
    """Return (commit, message) for every commit reachable from the revision; a cut-off history is refused."""
    if git(repository, "rev-parse", "--is-shallow-repository").strip() != "false":
        raise AttributionError("the clone is shallow, so older commit messages cannot be read")
    records = [record.partition("\n") for record in git(repository, "log", "-z", "--format=%H%n%B", "--end-of-options", revision, "--").split("\0") if record]
    if not records or any(not re.fullmatch(r"[0-9a-f]{40,64}", commit) for commit, _, _ in records):
        raise AttributionError(f"no commit messages could be read from {revision}")
    return [(commit, message) for commit, _, message in records]


def event_texts(path):
    """Return the title and text of the pull request an event file describes; other events carry none."""
    path = Path(path)
    if path.stat().st_size > MAX_EVENT_BYTES:
        raise AttributionError("the event file is too large")
    event = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(event, dict):
        raise AttributionError("the event file is not an object")
    request = event.get("pull_request")
    if request is None:
        return []
    if not isinstance(request, dict) or not isinstance(request.get("title"), str) or not isinstance(request.get("body") or "", str):
        raise AttributionError("the event names a pull request without a readable title and text")
    return [("pull request title", request["title"]), ("pull request text", request.get("body") or "")]


def check(repository, revision, event):
    commits = history(repository, revision)
    texts = event_texts(event) if event else []
    found = [finding for commit, message in commits for finding in scan(f"commit {commit[:12]}", message)]
    found += [finding for source, text in texts for finding in scan(source, text)]
    return len(commits), len(texts), found


def commit(repository, message):
    identity = ("-c", "user.name=Self Test", "-c", "user.email=self-test@example.invalid", "-c", "commit.gpgsign=false")
    git(repository, *identity, "commit", "--quiet", "--allow-empty", "--no-verify", "-m", message)


def self_test():
    """Prove the check against planted credits before its result on the real history is trusted."""
    lines = {
        "Co-authored-by: Claude Opus 5.5 <noreply@anthropic.com>": True,
        "co-authored-by: Somebody <noreply@anthropic.com>": True,
        "Co-Authored-By: Codex <codex@example.invalid>": True,
        "Assisted-by: GitHub Copilot": True,
        "\U0001f916 Generated with [Claude Code](https://claude.com/claude-code)": True,
        "Generated by Cursor": True,
        "Co-authored-by: renovate[bot] <29139614+renovate[bot]@users.noreply.github.com>": False,
        "Co-authored-by: Claude Dupont <claude@example.org>": False,
        "Signed-off-by: Devin Example <devin@example.org>": False,
        "This PR was generated with [Release Please](https://github.com/googleapis/release-please).": False,
        "docs: describe how Claude Code hooks are configured": False,
    }
    for line, expected in lines.items():
        if credits_tool(line) != expected:
            raise AttributionError(f"self-test: '{line}' should {'be refused' if expected else 'pass'}")
    with tempfile.TemporaryDirectory() as root:
        git(root, "init", "--quiet")
        commit(root, "feat: first change\n\nCo-authored-by: Kilian <kilian@example.org>")
        if check(root, "HEAD", None) != (1, 0, []):
            raise AttributionError("self-test: a clean history was not read as one clean commit")
        commit(root, "fix: second change\n\nCo-authored-by: Claude Opus 5.5 <noreply@anthropic.com>")
        count, _, found = check(root, "HEAD", None)
        if count != 2 or [(finding.number, finding.source[:7]) for finding in found] != [(3, "commit ")]:
            raise AttributionError("self-test: a planted trailer in the history was not reported")
    print(f"Attribution self-test: {len(lines) + 2} cases behaved as required")


def main(arguments=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", type=Path, default=Path("."))
    parser.add_argument("--revision", default="HEAD")
    parser.add_argument("--event", help="The event file of the run; a pull request's title and text are read from it.")
    parser.add_argument("--self-test", action="store_true")
    options = parser.parse_args(arguments)
    try:
        if options.self_test:
            self_test()
            return 0
        commits, texts, found = check(options.repository, options.revision, options.event)
    except (AttributionError, OSError, json.JSONDecodeError, UnicodeDecodeError) as error:
        print(f"Attribution check failed: {error}", file=sys.stderr)
        return 2
    for finding in found:
        print(f"{finding.source}, line {finding.number}: {finding.line}", file=sys.stderr)
    if found:
        print(f"Attribution check: {len(found)} line(s) credit a model or coding tool; remove them from the commit message or the pull request text.", file=sys.stderr)
        return 1
    print(f"Attribution check: {commits} commit message(s) and {texts} pull request text(s) credit no model or coding tool")
    return 0


if __name__ == "__main__":
    sys.exit(main())
