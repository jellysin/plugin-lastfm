"""Gate Gitleaks findings on fingerprint-scoped exceptions that expire."""

import argparse
import base64
import datetime
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path

EXCEPTIONS = ".gitleaksignore"
MAX_EXCEPTIONS = 16
MAX_EXCEPTION_BYTES = 16 * 1024
MAX_REPORT_BYTES = 16 * 1024 * 1024
LEAK_EXIT = 3
SCAN_TIMEOUT_SECONDS = 300
EXPIRY = re.compile(r"# expires (\d{4}-\d{2}-\d{2}): \S.*")
FINGERPRINT = re.compile(r"[0-9a-f]{40}:[^:*?\s]+:[a-z0-9-]+:[1-9][0-9]*")


class ScanError(ValueError):
    """The exception list is malformed or the scanner did not complete."""


@dataclass(frozen=True)
class Exemption:
    fingerprint: str
    expires: datetime.date

    def active(self, today):
        return today < self.expires


def parse_exceptions(text):
    """Read `# expires YYYY-MM-DD: reason` lines, each followed by one full fingerprint."""
    entries, expires = [], None
    for number, raw in enumerate(text.splitlines(), 1):
        line = raw.strip()
        if not line:
            continue
        if line.startswith("#"):
            match = EXPIRY.fullmatch(line)
            if match:
                if expires:
                    raise ScanError(f"line {number}: expiry without a fingerprint before it")
                expires = parse_date(match.group(1), number)
            continue
        if not FINGERPRINT.fullmatch(line):
            raise ScanError(f"line {number}: not a commit:path:rule:line fingerprint")
        if not expires:
            raise ScanError(f"line {number}: fingerprint without an expiry and reason")
        entries.append(Exemption(line, expires))
        expires = None
    if expires:
        raise ScanError("an expiry at the end names no fingerprint")
    if len(entries) > MAX_EXCEPTIONS or len({entry.fingerprint for entry in entries}) != len(entries):
        raise ScanError("exceptions are duplicated or exceed the supported count")
    return entries


def parse_date(value, number):
    try:
        return datetime.date.fromisoformat(value)
    except ValueError as error:
        raise ScanError(f"line {number}: invalid expiry date") from error


def load_exceptions(repository):
    path = Path(repository) / EXCEPTIONS
    if not path.is_file():
        return []
    if path.stat().st_size > MAX_EXCEPTION_BYTES:
        raise ScanError("the exception list is too large")
    return parse_exceptions(path.read_text(encoding="utf-8"))


def findings(gitleaks, repository):
    """Return every fingerprint Gitleaks reports with no exception applied."""
    located = subprocess.run(["git", "rev-parse", "--absolute-git-dir"], cwd=repository, capture_output=True, text=True, timeout=60, check=True)
    with tempfile.TemporaryDirectory() as scratch:
        # Gitleaks applies a .gitleaksignore in its source directory without regard to expiry,
        # so it reads the history through GIT_DIR from a directory that holds no list.
        source = Path(scratch) / "source"
        source.mkdir()
        report = Path(scratch) / "report.json"
        command = [
            str(Path(gitleaks).resolve()), "git", "--redact", "--no-banner", "--exit-code", str(LEAK_EXIT),
            "--gitleaks-ignore-path", str(source), "--report-format", "json", "--report-path", str(report), str(source),
        ]
        environment = {**os.environ, "GIT_DIR": located.stdout.strip()}
        result = subprocess.run(command, cwd=source, env=environment, capture_output=True, text=True, timeout=SCAN_TIMEOUT_SECONDS, check=False)
        if result.returncode not in (0, LEAK_EXIT):
            raise ScanError(f"Gitleaks did not complete (exit {result.returncode}): {result.stderr.strip()[-400:]}")
        if not report.is_file() or report.stat().st_size > MAX_REPORT_BYTES:
            raise ScanError("Gitleaks wrote no usable report")
        found = [item["Fingerprint"] for item in json.loads(report.read_text(encoding="utf-8"))]
    if bool(found) != (result.returncode == LEAK_EXIT):
        raise ScanError("the Gitleaks exit status contradicts its report")
    return found


def scan(gitleaks, repository, today):
    """Return (allowed, blocking, unused) fingerprints for one repository."""
    entries = load_exceptions(repository)
    active = {entry.fingerprint for entry in entries if entry.active(today)}
    found = findings(gitleaks, repository)
    allowed = sorted(item for item in found if item in active)
    blocking = sorted(item for item in found if item not in active)
    unused = sorted(entry.fingerprint for entry in entries if entry.fingerprint not in found)
    return allowed, blocking, unused


def git(repository, *arguments):
    identity = ["-c", "user.name=fixture", "-c", "user.email=fixture@invalid", "-c", "commit.gpgsign=false"]
    subprocess.run(["git", *identity, *arguments], cwd=repository, capture_output=True, text=True, timeout=60, check=True)


def planted_line():
    # Derived at run time so that this file holds no key-shaped literal of its own.
    value = base64.urlsafe_b64encode(hashlib.sha256(b"JellySin planted scan fixture").digest()).decode()[:40]
    return f'api_key = "{value}"\n'


def fixture(root, name, side_branch=False):
    repository = Path(root) / name
    repository.mkdir()
    git(repository, "init", "--quiet", "--initial-branch", "main")
    (repository / "README.md").write_text("fixture\n", encoding="utf-8")
    git(repository, "add", "README.md")
    git(repository, "commit", "--quiet", "--message", "fixture")
    if name == "clean":
        return repository
    if side_branch:
        git(repository, "switch", "--quiet", "--create", "side")
    (repository / "settings.py").write_text(planted_line(), encoding="utf-8")
    git(repository, "add", "settings.py")
    git(repository, "commit", "--quiet", "--message", "planted")
    if side_branch:
        git(repository, "switch", "--quiet", "main")
    return repository


def excepted(root, gitleaks, name, expires):
    repository = fixture(root, name)
    (fingerprint,) = findings(gitleaks, repository)
    (repository / EXCEPTIONS).write_text(f"# expires {expires.isoformat()}: fixture\n{fingerprint}\n", encoding="utf-8")
    return repository


def self_test(gitleaks, today):
    """Prove the gate against planted keys before its result on the real history is trusted."""
    day = datetime.timedelta(days=1)
    with tempfile.TemporaryDirectory() as root:
        cases = {
            "clean": (fixture(root, "clean"), 0),
            "planted on HEAD": (fixture(root, "head"), 1),
            "planted on a side branch": (fixture(root, "side", side_branch=True), 1),
            "unexpired exception": (excepted(root, gitleaks, "valid", today + day), 0),
            "exception expiring today": (excepted(root, gitleaks, "today", today), 1),
            "expired exception": (excepted(root, gitleaks, "expired", today - day), 1),
        }
        for label, (repository, expected) in cases.items():
            blocking = len(scan(gitleaks, repository, today)[1])
            if blocking != expected:
                raise ScanError(f"self-test '{label}': expected {expected} blocking finding(s), got {blocking}")
    print(f"Secret scan self-test: {len(cases)} cases behaved as required")


def main(arguments=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--gitleaks", type=Path, required=True)
    parser.add_argument("--repository", type=Path, default=Path("."))
    parser.add_argument("--self-test", action="store_true")
    options = parser.parse_args(arguments)
    today = datetime.datetime.now(datetime.UTC).date()
    try:
        if options.self_test:
            self_test(options.gitleaks, today)
            return 0
        allowed, blocking, unused = scan(options.gitleaks, options.repository, today)
    except (ScanError, OSError, subprocess.SubprocessError, json.JSONDecodeError, KeyError) as error:
        print(f"Secret scan failed: {error}", file=sys.stderr)
        return 2
    print(f"Secret scan: {len(allowed)} findings allowed by unexpired exceptions, {len(blocking)} blocking")
    for fingerprint in unused:
        print(f"Unused exception, remove it: {fingerprint}")
    for fingerprint in blocking:
        print(f"Blocking finding: {fingerprint}", file=sys.stderr)
    return 1 if blocking else 0


if __name__ == "__main__":
    sys.exit(main())
