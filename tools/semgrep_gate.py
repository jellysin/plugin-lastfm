"""Run the repository's Semgrep rules: the registry packs CI used before and the local rules in .config/semgrep.

`make verify-all`, the lefthook pre-commit job and the CI scanners job all start Semgrep through this file, so the
three run one pinned release (.config/semgrep/requirements.txt) with one rule set. With no file arguments it scans
every tracked source file; with arguments it scans those that have a scanned suffix. `--self-test` writes planted
cases to a temporary directory and requires the rules to report exactly the planted lines. The planted cases are not
committed, so the repository's own scanners never see them.
"""

import argparse
import json
import shutil
import subprocess  # nosec B404
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REQUIREMENTS = ROOT / ".config" / "semgrep" / "requirements.txt"
LOCAL_RULES = ROOT / ".config" / "semgrep"
PACKS = ("p/csharp", "p/typescript", "p/javascript", "p/python", "p/github-actions")
SUFFIXES = {".cs", ".js", ".mjs", ".cjs", ".ts", ".mts", ".py", ".yml", ".yaml"}
# Two scans (self-test, then the tree) plus the earlier steps fit the CI scanners job's 15 minutes; both took 25 s
# together on ubuntu-24.04 (run 41312005329).
SCAN_TIMEOUT_SECONDS = 300
MAX_TARGETS = 20000
MARK = "planted"

# The fixture-order check's first regex (#248, commit 7452095), which Codacy reported as a ReDoS shape. Kept as text
# so that no committed file holds it as a live regex.
FIXTURE_ORDER_REGEX = (
    r"^\s*(?:(?:\+\+|--)\s*state\b|state(?:\.[A-Za-z_$][\w$]*|\[[^\]]+\])+\s*(?:[-+*/%&|^]?=(?!=)|\+\+|--|"
    r"\.(?:push|pop|shift|unshift|splice|set|delete|clear|add)\()|Object\.assign\(\s*state\b)"
)
PLANTED = {
    "planted.mjs": "\n".join([
        f"const stateWrite = /{FIXTURE_ORDER_REGEX}/; // {MARK}",
        "export const racy = line => stateWrite.test(line); // " + MARK,
        "export const safe = line => /^state\\.[a-z]+ = /.test(line);",
        'export const built = new RegExp("^(a+)+$"); // ' + MARK,
        "",
    ]),
    "planted.py": "\n".join([
        "import re",
        'NESTED = re.compile("^(a+)+$")  # ' + MARK,
        'SAFE = re.compile("^[a-z]+$")',
        "",
    ]),
    "planted.cs": "\n".join([
        "using System.Text.RegularExpressions;",
        "public static class Planted",
        "{",
        '    public static readonly Regex Nested = new Regex("^(a+)+$"); // ' + MARK,
        '    public static readonly Regex Safe = new Regex("^(a+)+$", RegexOptions.NonBacktracking);',
        '    public static bool Static(string input) => Regex.IsMatch(input, "^(\\\\w+\\\\s?)*$"); // ' + MARK,
        '    public static bool Plain(string input) => Regex.IsMatch(input, "^[a-z]+$");',
        "}",
        "",
    ]),
}


# Semgrep 1.183.0 publishes wheels tagged cp310 to cp314 only (PyPI file list); the repository's .python-version is
# newer, and without a wheel the launcher builds the sdist, which has no semgrep-core binary. Raise this together with
# the version in the requirements file once a release ships wheels for a newer interpreter.
SEMGREP_PYTHON = "3.14"


class GateError(RuntimeError):
    """Semgrep did not run to completion, or the self-test found the rules wrong."""


def pinned_version(text: str, package: str = "semgrep", source: Path = REQUIREMENTS) -> str:
    """Return the package version the requirements file pins with `==`."""
    prefix = f"{package}=="
    versions = [line.split("==", 1)[1].strip() for line in text.splitlines() if line.startswith(prefix)]
    if len(versions) != 1 or not versions[0]:
        rel = source.relative_to(ROOT) if source.is_relative_to(ROOT) else source
        raise GateError(f"{rel}: expected one `{package}==<version>` line")
    return versions[0]


def launcher(version: str, package: str = "semgrep", python: str = SEMGREP_PYTHON) -> list[str]:
    """Return the command that starts the pinned tool; uvx first, pipx second, and say which one runs.

    Only these two launchers are accepted, each resolved once to an absolute path on PATH, the same trust any
    developer tool on the machine gets; the version comes from the committed requirements file.
    """
    uvx, pipx = shutil.which("uvx"), shutil.which("pipx")
    if uvx:
        command = [uvx, "--quiet", "--python", python, "--from", f"{package}=={version}", package]
    elif pipx:
        command = [pipx, "run", "--python", python, "--fetch-missing-python", "--spec", f"{package}=={version}", package]
    else:
        raise GateError(f"neither uvx nor pipx found; install uv (https://docs.astral.sh/uv/) to run {package}")
    print(f"{package}-gate: {package} {version} on Python {python} via {command[0]}", file=sys.stderr)
    return command


def scanned(paths: list[str]) -> list[str]:
    """Return the paths with a scanned suffix, in order.

    Targets are files of this repository (staged or tracked names): a path that resolves outside the repository, or a
    list longer than MAX_TARGETS, is refused. They reach Semgrep after `--`, as list elements without a shell, so a
    name cannot become an option or a command.
    """
    if len(paths) > MAX_TARGETS:
        raise GateError(f"{len(paths)} targets exceed the bound of {MAX_TARGETS}")
    outside = [path for path in paths if not (ROOT / path).resolve().is_relative_to(ROOT)]
    if outside:
        raise GateError(f"targets outside the repository: {', '.join(outside[:5])}")
    return [path for path in paths if Path(path).suffix in SUFFIXES]


def tracked() -> list[str]:
    """Return every tracked file of the repository; git is resolved once to an absolute path on PATH."""
    executable = shutil.which("git")
    if executable is None:
        raise GateError("git not found; the tracked files cannot be listed")
    # Fixed arguments, absolute executable path, no shell.
    # nosemgrep: python.lang.security.audit.dangerous-subprocess-use-audit.dangerous-subprocess-use-audit
    result = subprocess.run(  # nosec B603  # noqa: S603 - fixed argv with absolute git executable
        [executable, "ls-files", "-z"], cwd=ROOT, capture_output=True, check=True, timeout=60
    )
    return [name for name in result.stdout.decode().split("\0") if name]


def run(command: list[str], targets: list[str], packs: tuple[str, ...]) -> list[dict]:
    """Run Semgrep on the targets and return its findings; fail on scan errors and unexpected exit codes."""
    configs = [part for pack in packs for part in ("--config", pack)]
    arguments = [
        *command, "scan", "--metrics=off", "--disable-version-check", "--quiet", "--json",
        "--config", str(LOCAL_RULES), *configs, "--", *targets,
    ]
    # The launcher is uvx or pipx at an absolute path, the version comes from the committed requirements file, and the
    # targets are repository paths (scanned() refuses any other) after `--`; no shell. Tests: TargetTests, LauncherTests.
    # nosemgrep: python.lang.security.audit.dangerous-subprocess-use-audit.dangerous-subprocess-use-audit
    result = subprocess.run(  # nosec B603  # noqa: S603 - fixed launcher with validated target paths
        arguments, check=False, cwd=ROOT, capture_output=True, text=True, timeout=SCAN_TIMEOUT_SECONDS
    )
    if result.returncode not in (0, 1):
        raise GateError(f"semgrep exited {result.returncode}: {result.stderr.strip()[-2000:]}")
    report = json.loads(result.stdout)
    errors = report.get("errors", [])
    fatal = [error for error in errors if error.get("level") != "warn"]
    if fatal:
        messages = "; ".join(error.get("message", str(error))[:300] for error in fatal[:5])
        raise GateError(f"semgrep reported {len(fatal)} scan errors: {messages}")
    # Syntax Semgrep's parsers do not know yet (C# primary constructors, for one) leaves part of a file unscanned;
    # the gate names those files instead of passing them silently.
    unparsed = sorted({span["file"] for error in errors for span in error.get("spans", []) if "file" in span})
    if unparsed:
        print(f"semgrep-gate: {len(unparsed)} files only partly parsed: {', '.join(unparsed)}", file=sys.stderr)
    return report["results"]


def describe(finding: dict) -> str:
    """Return one line for a finding: path, line, rule and the first line of its message."""
    message = finding["extra"]["message"].strip().splitlines()[0]
    return f"{finding['path']}:{finding['start']['line']}: {finding['check_id']}: {message}"


def expected_lines(text: str) -> set[int]:
    """Return the numbers of the lines that carry the planted mark."""
    return {number for number, line in enumerate(text.splitlines(), 1) if line.rstrip().endswith(MARK)}


def self_test(command: list[str]) -> list[str]:
    """Return a problem for every planted case the local rules miss and every unmarked line they report."""
    problems = []
    with tempfile.TemporaryDirectory(prefix="semgrep-gate-") as directory:
        files = {}
        for name, text in PLANTED.items():
            path = Path(directory) / name
            path.write_text(text, encoding="utf-8")
            files[str(path)] = text
        findings = run(command, list(files), ())
        for path, text in files.items():
            reported = {finding["start"]["line"] for finding in findings if finding["path"] == path}
            name = Path(path).name
            problems += [f"{name}:{line}: planted case not reported" for line in sorted(expected_lines(text) - reported)]
            problems += [f"{name}:{line}: reported but not planted" for line in sorted(reported - expected_lines(text))]
    return problems


def main(argv: list[str]) -> int:
    """Scan the given files, or every tracked file, or run the self-test; return the exit status."""
    parser = argparse.ArgumentParser(description="Run the repository's pinned Semgrep rules.")
    parser.add_argument("--self-test", action="store_true", help="check the local rules against planted cases")
    parser.add_argument("files", nargs="*", help="files to scan; every tracked file when none is given")
    arguments = parser.parse_args(argv)
    try:
        command = launcher(pinned_version(REQUIREMENTS.read_text(encoding="utf-8")))
        if arguments.self_test:
            problems = self_test(command)
            for problem in problems:
                print(f"semgrep-gate self-test: {problem}", file=sys.stderr)
            return 1 if problems else 0
        targets = scanned(arguments.files or tracked())
        if not targets:
            return 0
        findings = run(command, targets, PACKS)
    except (GateError, subprocess.SubprocessError, OSError, json.JSONDecodeError) as error:
        print(f"semgrep-gate: {error}", file=sys.stderr)
        return 2
    for finding in findings:
        print(describe(finding), file=sys.stderr)
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
