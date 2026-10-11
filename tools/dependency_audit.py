"""Audit the web lock with development dependencies and track findings in one issue."""

import argparse
import json
import os
import re
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path

TITLE = "Dependency audit: vulnerable packages in the web lock"
MARKER = "<!-- dependency-audit -->"
LABEL = "dependencies"
MAX_FINDINGS = 50
MAX_REPORT_BYTES = 8 * 1024 * 1024
AUDIT_TIMEOUT_SECONDS = 180
FORGE_TIMEOUT_SECONDS = 60
ADVISORY = re.compile(r"https://github\.com/advisories/GHSA(-[0-9a-z]{4}){3}")
UNSAFE = re.compile(r"[^A-Za-z0-9@/._<>=~^ *-]")
SEVERITIES = ("info", "low", "moderate", "high", "critical")
PLANTED_NAME, PLANTED_VERSION = "brace-expansion", "5.0.9"
UNREACHABLE_REGISTRY = "https://registry.invalid"


class AuditError(ValueError):
    """npm or the forge did not complete, so there is no result to act on."""


@dataclass(frozen=True)
class Finding:
    package: str
    severity: str
    affected: str
    sources: tuple
    fix: str


def plain(value):
    return UNSAFE.sub("", str(value).replace("||", "or"))[:120]


def finding(name, entry):
    via = entry.get("via", [])
    advisories = {item.get("url", "") for item in via if isinstance(item, dict)}
    sources = sorted(url for url in advisories if ADVISORY.fullmatch(url)) or sorted(f"through `{plain(item)}`" for item in via if isinstance(item, str))
    fix = entry.get("fixAvailable")
    if isinstance(fix, dict):
        fix = f"{plain(fix.get('name'))} {plain(fix.get('version'))}" + (" (major)" if fix.get("isSemVerMajor") else "")
    else:
        fix = "yes" if fix is True else "no"
    severity = entry.get("severity") if entry.get("severity") in SEVERITIES else "unknown"
    return Finding(plain(name), severity, plain(entry.get("range")), tuple(sources), fix)


def audit(directory, production_only=False, registry=None):
    """Return the findings npm reports for one lock; an audit that did not complete raises."""
    command = ["npm", "audit", "--json", "--package-lock-only"]
    command += ["--omit=dev"] if production_only else []
    command += ["--registry", registry, "--fetch-retries=0"] if registry else []
    result = subprocess.run(command, cwd=directory, capture_output=True, text=True, timeout=AUDIT_TIMEOUT_SECONDS, check=False)
    if len(result.stdout) > MAX_REPORT_BYTES:
        raise AuditError("the npm audit report is too large")
    try:
        report = json.loads(result.stdout)
    except json.JSONDecodeError as error:
        raise AuditError(f"npm audit wrote no report (exit {result.returncode})") from error
    # npm exits 1 for findings and for its own errors; only a complete report tells them apart.
    if not isinstance(report, dict) or "error" in report or not isinstance(report.get("vulnerabilities"), dict):
        detail = report.get("message") or report.get("error") if isinstance(report, dict) else ""
        raise AuditError(f"npm audit did not complete (exit {result.returncode}): {str(detail)[:400]}")
    found = [finding(name, entry) for name, entry in sorted(report["vulnerabilities"].items())]
    counted = report.get("metadata", {}).get("vulnerabilities", {}).get("total")
    if not isinstance(counted, int) or bool(counted) != bool(found) or bool(found) != (result.returncode == 1):
        raise AuditError("the npm audit exit status contradicts its report")
    return found


def render(findings, revision):
    lines = [
        MARKER,
        f"The audit of `package-lock.json` at `{plain(revision)}` reports {len(findings)} vulnerable package(s).",
        "Development dependencies are included.",
        "",
        "| Package | Severity | Affected versions | Source | Fix available |",
        "| --- | --- | --- | --- | --- |",
    ]
    for item in findings[:MAX_FINDINGS]:
        lines.append(f"| `{item.package}` | {item.severity} | `{item.affected}` | {', '.join(item.sources) or 'not stated'} | {item.fix} |")
    if len(findings) > MAX_FINDINGS:
        lines.append(f"| {len(findings) - MAX_FINDINGS} more | | | | |")
    lines += ["", "Reproduce with `npm audit --package-lock-only`. Each run updates this issue; the first clean run closes it."]
    return "\n".join(lines) + "\n"


def forge(arguments, text=None):
    result = subprocess.run(["gh", *arguments], input=text, capture_output=True, text=True, timeout=FORGE_TIMEOUT_SECONDS, check=False)
    if result.returncode:
        raise AuditError(f"gh {' '.join(arguments[:2])} failed (exit {result.returncode}); the token needs `issues: write`: {result.stderr.strip()[-300:]}")
    return result.stdout


def tracked(repository):
    """Return the number of the open issue this tool owns, or None."""
    listed = json.loads(forge(["issue", "list", "--repo", repository, "--state", "open", "--label", LABEL, "--limit", "100", "--json", "number,title,body"]))
    owned = [item["number"] for item in listed if item["title"] == TITLE and item["body"].startswith(MARKER)]
    return min(owned, default=None)


def plan(findings, existing, repository, revision):
    """Return the gh arguments and the body for this result, or None when nothing changes."""
    if findings and existing is None:
        return ["issue", "create", "--repo", repository, "--title", TITLE, "--label", LABEL, "--body-file", "-"], render(findings, revision)
    if findings:
        return ["issue", "edit", str(existing), "--repo", repository, "--body-file", "-"], render(findings, revision)
    if existing is not None:
        comment = f"The audit is clean at `{plain(revision)}`."
        return ["issue", "close", str(existing), "--repo", repository, "--reason", "completed", "--comment", comment], None
    return None


def report(findings, repository, revision, dry_run):
    step = plan(findings, tracked(repository), repository, revision)
    if not step:
        return
    arguments, body = step
    if dry_run:
        print("Dry run, would run: gh " + " ".join(arguments))
        print(body or "", end="")
        return
    print(forge(arguments, body).strip())


def planted(root, name, section=None, lock=None):
    directory = Path(root) / name
    directory.mkdir()
    package = {"name": "planted", "version": "1.0.0"}
    packages = {"": package}
    if section:
        package[section] = {PLANTED_NAME: PLANTED_VERSION}
        resolved = f"https://registry.npmjs.org/{PLANTED_NAME}/-/{PLANTED_NAME}-{PLANTED_VERSION}.tgz"
        packages[f"node_modules/{PLANTED_NAME}"] = {"version": PLANTED_VERSION, "resolved": resolved, "dev": section == "devDependencies"}
    (directory / "package.json").write_text(json.dumps({**package, "private": True}), encoding="utf-8")
    text = lock or json.dumps({**package, "lockfileVersion": 3, "requires": True, "packages": packages})
    (directory / "package-lock.json").write_text(text, encoding="utf-8")
    return directory


def self_test():
    """Prove the audit against a planted vulnerable lock before its result on the real lock is trusted."""
    development, production = "devDependencies", "dependencies"
    with tempfile.TemporaryDirectory() as root:
        counted = {
            "clean lock": (planted(root, "clean"), False, 0),
            "vulnerable development dependency, full audit": (planted(root, "development", development), False, 1),
            "vulnerable development dependency, production gate": (planted(root, "gated", development), True, 0),
            "vulnerable production dependency, production gate": (planted(root, "production", production), True, 1),
        }
        for label, (directory, production_only, expected) in counted.items():
            found = [item.package for item in audit(directory, production_only)]
            if found != [PLANTED_NAME] * expected:
                raise AuditError(f"self-test '{label}': expected {expected} finding(s), got {found}")
        refused = {
            "unreadable lock": (planted(root, "unreadable", lock="{ not a lock"), None),
            "unreachable registry": (planted(root, "unreachable", development), UNREACHABLE_REGISTRY),
        }
        for label, (directory, registry) in refused.items():
            try:
                audit(directory, registry=registry)
            except AuditError:
                continue
            raise AuditError(f"self-test '{label}': an audit that cannot complete reported a result")
    print(f"Dependency audit self-test: {len(counted) + len(refused)} cases behaved as required")


def main(arguments=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, default=Path("."))
    parser.add_argument("--repository", default=os.environ.get("GITHUB_REPOSITORY"))
    parser.add_argument("--revision", default=os.environ.get("GITHUB_SHA", "an unknown revision"))
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    options = parser.parse_args(arguments)
    try:
        if options.self_test:
            self_test()
            return 0
        if not options.repository:
            raise AuditError("name the repository with --repository or GITHUB_REPOSITORY")
        found = audit(options.directory)
        print(f"Dependency audit: {len(found)} vulnerable package(s)")
        report(found, options.repository, options.revision, options.dry_run)
    except (AuditError, OSError, subprocess.SubprocessError, json.JSONDecodeError, KeyError, TypeError) as error:
        print(f"Dependency audit failed: {error}", file=sys.stderr)
        return 2
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main())
