"""Run the repository's Ruff rules under the Codacy rule set.

The CI scanners job starts Ruff through this file, using the pinned release in .config/ruff/requirements.txt and
rules in ruff.toml. With no file arguments it scans every tracked Python file; with arguments it scans those that
have the .py suffix. `--self-test` writes planted cases to a temporary directory and requires the rules to report
exactly the planted lines. The planted cases are not committed, so the repository's own scanners never see them.
Every per-file exception in ruff.toml carries a dated note on the line above it; the gate fails once one expires.
"""

import argparse
from collections.abc import Callable
from datetime import date
import json
import os
from pathlib import Path
import re
import subprocess  # nosec B404
import sys
import tempfile
import tomllib

from semgrep_gate import (
    GateError,
    MARK,
    expected_lines,
    launcher,
    pinned_version,
    read_bounded,
    scanned,
    tracked,
)

ROOT = Path(__file__).resolve().parents[1]
CONFIG = ROOT / "ruff.toml"
REQUIREMENTS = ROOT / ".config" / "ruff" / "requirements.txt"
MAX_REPORT_BYTES = 16 * 1024 * 1024
MAX_CONFIG_BYTES = 256 * 1024
MAX_PRINTED = 200
CHECK_TIMEOUT_SECONDS = 120
# Ruff 0.17.4 skips the executable-bit rules under WSL outside a container: cgroup marks from
# crates/ruff_linter/src/rules/flake8_executable/helpers.rs; recheck them when the Ruff pin moves.
CONTAINER_MARKS = ("docker", "/container", "/lxc", "kubepods")
MAX_PROC_BYTES = 64 * 1024
EXCEPTION_NOTE = re.compile(r"# expires (\d{4}-\d{2}-\d{2}) \(MEL [ABCD], entered \d{4}-\d{2}-\d{2}\): \S.*")

PLANTED_PY = "\n".join([
    f"#!/usr/bin/env python3  # {MARK}",
    "import subprocess",
    "",
    'text = "echo safe"',
    f"subprocess.call(text, shell=True)  # {MARK}",
    f"eval(text)  # {MARK}",
    "try:",
    "    pass",
    f"except:  # {MARK}",
    "    pass",
    "eval(text)  # noqa: S307 - planted suppression",
    "",
])


def exception_problems(text: str, today: date) -> list[str]:
    """Return a problem for every per-file exception in the ruff.toml text without a dated note or past its date.

    The line above each `[lint.per-file-ignores]` entry must read
    `# expires YYYY-MM-DD (MEL <A-D>, entered YYYY-MM-DD): <reason>`; an entry is still valid on its expiry day.
    A malformed file or an invalid calendar date raises GateError naming ruff.toml.
    """
    try:
        data = tomllib.loads(text)
    except tomllib.TOMLDecodeError as error:
        raise GateError(f"ruff.toml: not valid TOML: {error}") from error
    lines = text.splitlines()
    problems = []
    for path in data.get("lint", {}).get("per-file-ignores", {}):
        index = next((number for number, line in enumerate(lines) if line.startswith(f'"{path}" =')), 0)
        note = EXCEPTION_NOTE.fullmatch(lines[index - 1]) if index else None
        if note is None:
            problems.append(f"{path}: exception without a `# expires YYYY-MM-DD (MEL <A-D>, entered ...): ` note")
        else:
            try:
                expiry = date.fromisoformat(note.group(1))
            except ValueError as error:
                raise GateError(
                    f"ruff.toml: {path}: expiry date {note.group(1)} is not a calendar date"
                ) from error
            if expiry < today:
                problems.append(f"{path}: exception expired on {note.group(1)}")
    return problems


def run(command: list[str], targets: list[str]) -> list[dict]:
    """Run Ruff on the targets and return its findings; fail on scan errors and unexpected exit codes."""
    if CONFIG.is_symlink() or not CONFIG.exists():
        rel = CONFIG.relative_to(ROOT) if CONFIG.is_relative_to(ROOT) else CONFIG
        raise GateError(f"{rel}: configuration file missing or a symlink")
    with tempfile.TemporaryDirectory(prefix="ruff-gate-") as directory:
        report_path = Path(directory) / "report.json"
        stderr_path = Path(directory) / "stderr.txt"
        arguments = [
            *command, "check", "--config", str(CONFIG), "--no-cache",
            "--output-format", "json", "--output-file", str(report_path), "--", *targets,
        ]
        with open(stderr_path, "w", encoding="utf-8") as stderr_file:
            # The launcher is uvx or pipx at an absolute path, the version comes from the committed requirements file,
            # and the targets are repository paths (scanned() refuses any other) after `--`; no shell.
            # nosemgrep: python.lang.security.audit.dangerous-subprocess-use-audit.dangerous-subprocess-use-audit
            result = subprocess.run(  # nosec B603  # noqa: S603 - fixed launcher and config with validated target paths
                arguments, check=False, cwd=ROOT, stdout=subprocess.DEVNULL, stderr=stderr_file,
                timeout=CHECK_TIMEOUT_SECONDS,
            )
        if result.returncode not in (0, 1):
            stderr_size = stderr_path.stat().st_size
            with open(stderr_path, "rb") as handle:
                if stderr_size > 2000:
                    handle.seek(stderr_size - 2000)
                tail = handle.read(2000).decode("utf-8", errors="replace").strip()
            raise GateError(f"ruff exited {result.returncode}: {tail}")
        if not report_path.is_file():
            raise GateError("ruff produced no report file")
        report_size = report_path.stat().st_size
        if report_size > MAX_REPORT_BYTES:
            raise GateError(f"ruff report size {report_size} exceeds the limit of {MAX_REPORT_BYTES} bytes")
        return json.loads(report_path.read_text(encoding="utf-8"))


def describe(finding: dict) -> str:
    """Return one line for a finding: relative path, row, column, rule code, and message."""
    raw_path = Path(finding["filename"])
    try:
        rel_path = raw_path.resolve().relative_to(ROOT)
    except ValueError:
        rel_path = raw_path
    location = finding.get("location", {})
    row = location.get("row", 1)
    col = location.get("column", 1)
    code = finding.get("code", "")
    message = finding.get("message", "")
    return f"{rel_path}:{row}:{col}: {code} {message}"


def planted_lines(text: str, bit_checked: bool) -> set[int]:
    """Return the numbers of the marked lines Ruff must report; without line 1 where the bit is unchecked."""
    lines = expected_lines(text)
    if not bit_checked:
        return lines - {1}
    return lines


def proc_text(path: str) -> str | None:
    """Return the text of a small system file, or None when it cannot be read."""
    try:
        return read_bounded(path, MAX_PROC_BYTES)
    except (OSError, UnicodeDecodeError, GateError):
        return None


def checks_executable_bit(posix: bool, linux: bool, kernel: str | None, container: bool) -> bool:
    """Return whether Ruff checks the executable bit: on Unix-like systems, except WSL outside a container."""
    wsl = linux and kernel is not None and "microsoft" in kernel.lower() and not container
    return posix and not wsl


def host_checks_executable_bit(
    read: Callable[[str], str | None] = proc_text,
    exists: Callable[[str], bool] = os.path.exists,
    posix: bool = os.name == "posix",
    platform: str = sys.platform,
) -> bool:
    """Read the facts Ruff's WSL detection reads on this host and return whether it checks the executable bit."""
    kernel = read("/proc/sys/kernel/osrelease")
    if kernel is None:
        kernel = read("/proc/version")
    cgroup = (read("/proc/self/cgroup") or "").lower()
    container = (
        exists("/.dockerenv") or exists("/run/.containerenv") or any(mark in cgroup for mark in CONTAINER_MARKS)
    )
    return checks_executable_bit(posix, platform.startswith("linux"), kernel, container)


def self_test(command: list[str]) -> list[str]:
    """Return a problem for every planted case the rules miss and every unmarked line they report."""
    problems = []
    with tempfile.TemporaryDirectory(prefix="ruff-gate-") as directory:
        path = Path(directory) / "planted.py"
        path.write_text(PLANTED_PY, encoding="utf-8")
        findings = run(command, [str(path)])
        reported = {finding["location"]["row"] for finding in findings}
        expected = planted_lines(PLANTED_PY, host_checks_executable_bit())
        name = path.name
        problems += [f"{name}:{line}: planted case not reported" for line in sorted(expected - reported)]
        problems += [f"{name}:{line}: reported but not planted" for line in sorted(reported - expected)]
    return problems


def main(argv: list[str]) -> int:
    """Scan the given files, or every tracked Python file, or run the self-test; return the exit status."""
    parser = argparse.ArgumentParser(description="Run the repository's pinned Ruff rules under the Codacy rule set.")
    parser.add_argument("--self-test", action="store_true", help="check the rules against planted cases")
    parser.add_argument("files", nargs="*", help="files to scan; every tracked Python file when none is given")
    arguments = parser.parse_args(argv)
    try:
        version = pinned_version(read_bounded(REQUIREMENTS, 64 * 1024), package="ruff", source=REQUIREMENTS)
        command = launcher(version, package="ruff")
        expired = exception_problems(read_bounded(CONFIG, MAX_CONFIG_BYTES), date.today())
        for problem in expired:
            print(f"ruff-gate: {problem}", file=sys.stderr)
        if expired:
            return 1
        if arguments.self_test:
            problems = self_test(command)
            for problem in problems:
                print(f"ruff-gate self-test: {problem}", file=sys.stderr)
            return 1 if problems else 0
        targets = scanned(arguments.files or tracked(), suffixes=frozenset({".py"}))
        if not targets:
            return 0
        findings = run(command, targets)
    except (GateError, subprocess.SubprocessError, OSError, ValueError) as error:
        print(f"ruff-gate: {error}", file=sys.stderr)
        return 2
    for finding in findings[:MAX_PRINTED]:
        print(describe(finding), file=sys.stderr)
    if len(findings) > MAX_PRINTED:
        print(f"ruff-gate: {len(findings) - MAX_PRINTED} more findings not shown", file=sys.stderr)
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
