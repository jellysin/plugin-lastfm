"""Run the repository's Ruff rules under the Codacy rule set.

The CI scanners job starts Ruff through this file, using the pinned release in .config/ruff/requirements.txt and
rules in ruff.toml. With no file arguments it scans every tracked Python file; with arguments it scans those that
have the .py suffix. `--self-test` writes planted cases to a temporary directory and requires the rules to report
exactly the planted lines. The planted cases are not committed, so the repository's own scanners never see them.
Every per-file exception in ruff.toml carries a dated note on the line above it; the gate fails once one expires.
"""

import argparse
from datetime import date
import json
from pathlib import Path
import re
import subprocess  # nosec B404
import sys
import tempfile
import tomllib

from semgrep_gate import GateError, launcher, pinned_version, tracked

ROOT = Path(__file__).resolve().parents[1]
CONFIG = ROOT / "ruff.toml"
REQUIREMENTS = ROOT / ".config" / "ruff" / "requirements.txt"
MAX_TARGETS = 20000
CHECK_TIMEOUT_SECONDS = 120
MARK = "planted"
EXCEPTION_NOTE = re.compile(r"# expires (\d{4}-\d{2}-\d{2}) \(MEL [ABCD], entered \d{4}-\d{2}-\d{2}\): \S.*")

PLANTED_PY = "\n".join([
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


def python_files(paths: list[str]) -> list[str]:
    """Return the paths with the .py suffix, in order.

    Targets are files of this repository (staged or tracked names): a path that resolves outside the repository, or a
    list longer than MAX_TARGETS, is refused. They reach Ruff after `--`, as list elements without a shell, so a
    name cannot become an option or a command.
    """
    if len(paths) > MAX_TARGETS:
        raise GateError(f"{len(paths)} targets exceed the bound of {MAX_TARGETS}")
    outside = [path for path in paths if not (ROOT / path).resolve().is_relative_to(ROOT)]
    if outside:
        raise GateError(f"targets outside the repository: {', '.join(outside[:5])}")
    return [path for path in paths if Path(path).suffix == ".py"]


def exception_problems(text: str, today: date) -> list[str]:
    """Return a problem for every per-file exception in the ruff.toml text without a dated note or past its date.

    The line above each `[lint.per-file-ignores]` entry must read
    `# expires YYYY-MM-DD (MEL <A-D>, entered YYYY-MM-DD): <reason>`; an entry is still valid on its expiry day.
    """
    lines = text.splitlines()
    problems = []
    for path in tomllib.loads(text).get("lint", {}).get("per-file-ignores", {}):
        index = next((number for number, line in enumerate(lines) if line.startswith(f'"{path}" =')), 0)
        note = EXCEPTION_NOTE.fullmatch(lines[index - 1]) if index else None
        if note is None:
            problems.append(f"{path}: exception without a `# expires YYYY-MM-DD (MEL <A-D>, entered ...): ` note")
        elif date.fromisoformat(note.group(1)) < today:
            problems.append(f"{path}: exception expired on {note.group(1)}")
    return problems


def run(command: list[str], targets: list[str]) -> list[dict]:
    """Run Ruff on the targets and return its findings; fail on scan errors and unexpected exit codes."""
    if CONFIG.is_symlink() or not CONFIG.exists():
        rel = CONFIG.relative_to(ROOT) if CONFIG.is_relative_to(ROOT) else CONFIG
        raise GateError(f"{rel}: configuration file missing or a symlink")
    arguments = [
        *command, "check", "--config", str(CONFIG), "--no-cache", "--output-format", "json", "--", *targets,
    ]
    # The launcher is uvx or pipx at an absolute path, the version comes from the committed requirements file, and the
    # targets are repository paths (python_files() refuses any other) after `--`; no shell.
    # nosemgrep: python.lang.security.audit.dangerous-subprocess-use-audit.dangerous-subprocess-use-audit
    result = subprocess.run(  # nosec B603  # noqa: S603 - fixed launcher and config with validated target paths
        arguments, check=False, cwd=ROOT, capture_output=True, text=True, timeout=CHECK_TIMEOUT_SECONDS
    )
    if result.returncode not in (0, 1):
        raise GateError(f"ruff exited {result.returncode}: {result.stderr.strip()[-2000:]}")
    return json.loads(result.stdout)


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


def expected_lines(text: str) -> set[int]:
    """Return the numbers of the lines that carry the planted mark."""
    return {number for number, line in enumerate(text.splitlines(), 1) if line.rstrip().endswith(MARK)}


def self_test(command: list[str]) -> list[str]:
    """Return a problem for every planted case the rules miss and every unmarked line they report."""
    problems = []
    with tempfile.TemporaryDirectory(prefix="ruff-gate-") as directory:
        path = Path(directory) / "planted.py"
        path.write_text(PLANTED_PY, encoding="utf-8")
        findings = run(command, [str(path)])
        reported = {finding["location"]["row"] for finding in findings}
        expected = expected_lines(PLANTED_PY)
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
        version = pinned_version(REQUIREMENTS.read_text(encoding="utf-8"), package="ruff", source=REQUIREMENTS)
        command = launcher(version, package="ruff")
        expired = exception_problems(CONFIG.read_text(encoding="utf-8"), date.today())
        for problem in expired:
            print(f"ruff-gate: {problem}", file=sys.stderr)
        if expired:
            return 1
        if arguments.self_test:
            problems = self_test(command)
            for problem in problems:
                print(f"ruff-gate self-test: {problem}", file=sys.stderr)
            return 1 if problems else 0
        targets = python_files(arguments.files or tracked())
        if not targets:
            return 0
        findings = run(command, targets)
    except (GateError, subprocess.SubprocessError, OSError, ValueError) as error:
        print(f"ruff-gate: {error}", file=sys.stderr)
        return 2
    for finding in findings:
        print(describe(finding), file=sys.stderr)
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
