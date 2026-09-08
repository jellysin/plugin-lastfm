"""Gate one Cobertura run using unique production source lines, not report totals."""

import argparse
import os
import re
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path

PROJECT = "JellySin.Plugin.Lastfm"
SECURITY_AREAS = ("api", "configuration", "transport")
MAX_REPORT_BYTES = 16 * 1024 * 1024
MAX_REPORTS = 256
MAX_DIRECTORIES = 4096
MAX_FILES = 32768
MAX_LINES = 100_000


class CoverageError(ValueError):
    """A coverage report is absent, malformed, or insufficient."""


@dataclass(frozen=True)
class Counts:
    covered: int
    total: int

    @property
    def percentage(self):
        return 100 * self.covered / self.total if self.total else 0.0

    def meets(self, threshold):
        return self.total > 0 and self.covered * 100 >= threshold * self.total


@dataclass(frozen=True)
class Coverage:
    overall: Counts
    security: Counts
    areas: dict[str, Counts]

    def require_thresholds(self):
        missing = [area for area, counts in self.areas.items() if counts.total == 0]
        if missing:
            raise CoverageError("Missing security source areas: " + ", ".join(missing))
        if not self.overall.meets(70) or not self.security.meets(85):
            raise CoverageError(
                "Coverage requires 70% overall and 85% combined Api/Configuration/Transport lines"
            )


def select_report(location):
    path = Path(location)
    if path.is_symlink():
        raise CoverageError("Coverage input must not be a symbolic link")
    if path.is_file():
        return path
    if not path.is_dir():
        raise CoverageError("Coverage input does not exist")
    reports = []
    file_count = 0
    directory_count = 0
    for directory, directories, files in os.walk(path, followlinks=False):
        directory_count += 1
        file_count += len(files)
        if directory_count > MAX_DIRECTORIES or file_count > MAX_FILES:
            raise CoverageError("Coverage discovery exceeds its directory/file limit")
        directories[:] = [
            name for name in directories if not (Path(directory) / name).is_symlink()
        ]
        for name in files:
            if not name.endswith(".cobertura.xml"):
                continue
            candidate = Path(directory) / name
            if candidate.is_symlink():
                raise CoverageError("Coverage reports must not be symbolic links")
            reports.append(candidate)
            if len(reports) > MAX_REPORTS:
                raise CoverageError(
                    "Too many coverage reports; use a fresh results directory"
                )
    if not reports:
        raise CoverageError("No Cobertura report found")
    # Never combine stale runs: mtime selects the latest completed collector output.
    # A deterministic filename tie-breaker handles filesystems with coarse timestamps.
    return max(
        reports, key=lambda report: (report.stat().st_mtime_ns, report.as_posix())
    )


def source_name(value):
    if not isinstance(value, str) or not value or len(value) > 4096:
        raise CoverageError("Missing or oversized source filename")
    if any(ord(character) < 32 for character in value):
        raise CoverageError("Invalid source filename")
    parts = [
        part for part in value.replace("\\", "/").split("/") if part not in ("", ".")
    ]
    if ".." in parts:
        raise CoverageError("Source filename contains traversal")
    lowered = [part.casefold() for part in parts]
    marker = PROJECT.casefold()
    if marker in lowered:
        index = lowered.index(marker)
        parts = parts[index + 1 :]
    elif value.startswith(("/", "\\")) or re.match(r"^[A-Za-z]:", value):
        raise CoverageError(
            "Absolute source path does not identify the production project"
        )
    if not parts or not parts[-1].casefold().endswith(".cs"):
        raise CoverageError("Production report contains an unexpected source file")
    # This repository is portable to Windows; case-only paths identify the same file.
    return "/".join(parts).casefold()


def nonnegative_integer(value, label, positive=False):
    if not isinstance(value, str) or not re.fullmatch(r"[0-9]{1,19}", value):
        raise CoverageError("Invalid " + label)
    number = int(value)
    if number > 2**63 - 1 or positive and number == 0:
        raise CoverageError("Invalid " + label)
    return number


def load_xml(path):
    with Path(path).open("rb") as stream:
        data = stream.read(MAX_REPORT_BYTES + 1)
    if len(data) > MAX_REPORT_BYTES:
        raise CoverageError("Coverage report exceeds 16 MiB")
    try:
        document = data.decode("utf-8-sig")
        upper = document.upper()
        if "<!DOCTYPE" in upper or "<!ENTITY" in upper:
            raise CoverageError("DTD/entity declarations are not allowed")
        root = ET.fromstring(document)
    except (ET.ParseError, UnicodeError) as error:
        raise CoverageError("Invalid UTF-8 coverage XML") from error
    if root.tag != "coverage":
        raise CoverageError("Expected a Cobertura coverage document")
    return root


def production_lines(root):
    lines = {}
    line_elements = 0
    packages = root.findall("./packages/package")
    if len(packages) > 256:
        raise CoverageError("Too many coverage packages")
    for package in packages:
        if package.get("name") != PROJECT:
            continue
        for definition in package.findall("./classes/class"):
            filename = source_name(definition.get("filename"))
            direct_lines = definition.find("./lines")
            if direct_lines is None:
                raise CoverageError("A production class has no source-line inventory")
            for line in direct_lines.findall("./line"):
                line_elements += 1
                if line_elements > MAX_LINES:
                    raise CoverageError(
                        "Coverage report contains too many line entries"
                    )
                number = nonnegative_integer(
                    line.get("number"), "source line number", positive=True
                )
                hits = nonnegative_integer(line.get("hits"), "line hit count")
                key = filename, number
                # Async state machines and nested classes can repeat source lines.
                lines[key] = lines.get(key, False) or hits > 0
    if not lines:
        raise CoverageError("No production source lines found")
    return lines


def measure(path):
    lines = production_lines(load_xml(path))
    overall = Counts(sum(lines.values()), len(lines))
    areas = {}
    for area in SECURITY_AREAS:
        values = [
            hit
            for (name, _number), hit in lines.items()
            if name.split("/", 1)[0] == area
        ]
        areas[area] = Counts(sum(values), len(values))
    security = Counts(
        sum(area.covered for area in areas.values()),
        sum(area.total for area in areas.values()),
    )
    return Coverage(overall, security, areas)


def main(arguments=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "reports",
        nargs="?",
        default="build/coverage",
        help="One report or a collector results directory",
    )
    args = parser.parse_args(arguments)
    try:
        report = select_report(args.reports)
        coverage = measure(report)
        print("Coverage report:", report)
        for name, counts in (
            ("Overall", coverage.overall),
            ("Security", coverage.security),
            *coverage.areas.items(),
        ):
            print(
                f"{name}: {counts.covered}/{counts.total} unique lines ({counts.percentage:.2f}%)"
            )
        coverage.require_thresholds()
    except CoverageError as error:
        print("Coverage failed: " + str(error), file=sys.stderr)
        return 1
    except OSError:
        print("Coverage failed: report I/O error", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
