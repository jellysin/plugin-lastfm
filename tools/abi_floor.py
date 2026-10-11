"""Keep every Jellyfin package reference at the ABI floor recorded in plugin.json."""

import argparse
import json
import os
import re
import sys
from pathlib import Path
from xml.etree import ElementTree

DESCRIPTOR = "plugin.json"
RUNTIME = "runtime-dependencies.json"
HOST_PREFIX = "Jellyfin."
TARGET_ABI = re.compile(r"(\d+\.\d+\.\d+)\.0")
SKIPPED = {"bin", "obj", "build", "dist", "node_modules"}
MAX_PROJECTS = 64
MAX_FILE_BYTES = 1024 * 1024


class FloorError(ValueError):
    """The descriptor or a project cannot be read, so there is no result to act on."""


def read(path):
    if path.stat().st_size > MAX_FILE_BYTES:
        raise FloorError(f"{path.name} is too large")
    return path.read_text(encoding="utf-8")


def floor(root):
    """Return the package version of the lowest Jellyfin server the descriptor accepts."""
    match = TARGET_ABI.fullmatch(str(json.loads(read(Path(root) / DESCRIPTOR)).get("targetAbi")))
    if not match:
        raise FloorError(f"{DESCRIPTOR} names no targetAbi of the form X.Y.Z.0")
    return match.group(1)


def projects(root):
    found = []
    for directory, names, files in os.walk(root):
        names[:] = sorted(name for name in names if name not in SKIPPED and not name.startswith("."))
        found += [Path(directory) / name for name in sorted(files) if name.endswith(".csproj")]
        if len(found) > MAX_PROJECTS:
            raise FloorError("more project files than the check supports")
    return found


def references(root):
    """Return (file, package, version) for every Jellyfin package a project or the runtime list names."""
    listed = []
    for path in projects(root):
        try:
            elements = ElementTree.fromstring(read(path)).iter("PackageReference")
        except ElementTree.ParseError as error:
            raise FloorError(f"{path.name} is not a readable project file") from error
        for element in elements:
            if element.get("Include", "").startswith(HOST_PREFIX):
                listed.append((path.relative_to(root).as_posix(), element.get("Include"), element.get("Version")))
    compiled = len(listed)
    for entry in json.loads(read(Path(root) / RUNTIME))["runtimeDependencies"]:
        if entry["id"].startswith(HOST_PREFIX):
            listed.append((RUNTIME, entry["id"], entry.get("version")))
    # A check that finds nothing to compare would pass on a renamed package or a moved project.
    if not compiled or compiled == len(listed):
        raise FloorError("no Jellyfin reference found in the projects or in the runtime list")
    return listed


def mismatches(root):
    """Return the references that would bind the plugin to a newer server than the floor."""
    expected = floor(root)
    return [f"{file}: {package} {version}, the floor is {expected}" for file, package, version in references(root) if version != expected]


def main(arguments=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, default=Path("."))
    options = parser.parse_args(arguments)
    try:
        wrong = mismatches(options.directory)
    except (FloorError, OSError, json.JSONDecodeError, KeyError, TypeError) as error:
        print(f"ABI floor check failed: {error}", file=sys.stderr)
        return 2
    for line in wrong:
        print(f"Above or below the ABI floor: {line}", file=sys.stderr)
    print(f"ABI floor: {len(wrong)} reference(s) differ from {DESCRIPTOR}")
    return 1 if wrong else 0


if __name__ == "__main__":
    sys.exit(main())
