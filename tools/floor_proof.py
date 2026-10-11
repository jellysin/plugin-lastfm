#!/usr/bin/env python3
"""Show that the pinned release helper refuses a plugin reference above and below the ABI floor."""

import argparse
import copy
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path

PROJECT = Path("src/JellySin.Plugin.Lastfm")
SOURCES = {
    "nuget-lock-path": PROJECT / "packages.lock.json",
    "nuget-assets-path": PROJECT / "obj/project.assets.json",
    "runtime-dependencies-path": Path("runtime-dependencies.json"),
}
PUBLISHED = PROJECT / "bin/Release/net10.0"
REFERENCE = "Jellyfin.Controller"
REFUSAL = "Jellyfin reference differs from the target ABI floor"
ARTIFACTS = 4
MAX_FILE_BYTES = 8 * 1024 * 1024
TIMEOUT_SECONDS = 120


class ProofError(RuntimeError):
    """The proof could not be run, so it says nothing about the helper."""


def load(path):
    if path.stat().st_size > MAX_FILE_BYTES:
        raise ProofError(f"{path.name} is too large")
    return json.loads(path.read_text(encoding="utf-8"))


def referenced(documents):
    """Return the release the plugin references the server at, as the runtime list records it."""
    listed = [entry["version"] for entry in documents["runtime-dependencies-path"]["runtimeDependencies"] if entry["id"] == REFERENCE]
    if len(listed) != 1 or len(listed[0].split(".")) != 3 or not all(part.isdecimal() for part in listed[0].split(".")):
        raise ProofError(f"the runtime list names no single X.Y.Z release of {REFERENCE}")
    return listed[0]


def moved(documents, version):
    """Return copies of the lock, the restored graph and the runtime list that reference the server at another release."""
    current = referenced(documents)
    changed = copy.deepcopy(documents)
    runtime = changed["runtime-dependencies-path"]
    framework = runtime["targetFramework"]
    for entry in runtime["runtimeDependencies"]:
        if entry["id"] == REFERENCE:
            entry["version"] = version
    locked = changed["nuget-lock-path"]["dependencies"][framework][REFERENCE]
    locked["resolved"], locked["requested"] = version, f"[{version}, )"
    restored = changed["nuget-assets-path"]
    for graph in (restored["targets"][framework], restored["libraries"]):
        graph[f"{REFERENCE}/{version}"] = graph.pop(f"{REFERENCE}/{current}")
    return changed


def cases(documents):
    """Return the references to ask about: the repository's own, which is the floor, and one on each side of it."""
    current = referenced(documents)
    major, minor, _ = (int(part) for part in current.split("."))
    return {"at the floor": current, "above the floor": f"{major}.{minor + 1}.0", "below the floor": f"{major - 1}.0.0"}


def ask(tooling, root, documents, scratch):
    """Run the helper's package command on the given references; return its status, its words and what it wrote."""
    scratch.mkdir()
    command = [sys.executable, str(tooling / "tools.py"), "package", "--publish-directory", str(PUBLISHED),
               "--output-directory", str(scratch / "out")]
    for option, document in documents.items():
        (scratch / f"{option}.json").write_text(json.dumps(document), encoding="utf-8")
        command += [f"--{option}", str(scratch / f"{option}.json")]
    # Without the runner's output file the helper's step outputs do not reach this job.
    environment = {name: value for name, value in os.environ.items() if name != "GITHUB_OUTPUT"}
    done = subprocess.run(command, cwd=root, env=environment, capture_output=True, text=True, timeout=TIMEOUT_SECONDS, check=False)
    written = sorted(path.name for path in (scratch / "out").iterdir()) if (scratch / "out").is_dir() else []
    return done.returncode, done.stdout + done.stderr, written


def failures(tooling, root):
    """Return what the helper did other than package the floor and refuse both planted references for the floor's sake."""
    documents = {option: load(root / path) for option, path in SOURCES.items()}
    wrong = []
    with tempfile.TemporaryDirectory(prefix="jellysin-floor-") as temporary:
        for number, (label, version) in enumerate(cases(documents).items()):
            status, words, written = ask(tooling, root, moved(documents, version), Path(temporary) / str(number))
            if label == "at the floor":
                # A helper that packages nothing would refuse the planted references for a reason of its own.
                expected = status == 0 and len(written) == ARTIFACTS
            else:
                expected = status != 0 and REFUSAL in words and not written
            print(f"{REFERENCE} {version}, {label}: {'as required' if expected else 'NOT as required'} (exit {status}, {len(written)} file(s) written)")
            if not expected:
                wrong.append(label)
    return wrong


def main(arguments=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tooling-directory", type=Path, required=True)
    parser.add_argument("--directory", type=Path, default=Path("."))
    options = parser.parse_args(arguments)
    try:
        wrong = failures(options.tooling_directory.resolve(strict=True), options.directory.resolve(strict=True))
    except (ProofError, OSError, json.JSONDecodeError, KeyError, TypeError, subprocess.SubprocessError) as error:
        print(f"Floor proof could not run: {error if isinstance(error, ProofError) else type(error).__name__}", file=sys.stderr)
        return 2
    if wrong:
        print(f"The release helper does not hold the plugin's server reference at the floor: {', '.join(wrong)}", file=sys.stderr)
        return 1
    print("The release helper packages the floor and refuses a reference above and below it.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
