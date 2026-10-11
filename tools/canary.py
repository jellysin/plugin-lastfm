"""Name, guard and package a canary build of main: an installable plugin that cannot pass as a release."""

import argparse
import hashlib
import json
import os
import re
import sys
import zipfile
from pathlib import Path

DESCRIPTOR = "plugin.json"
WORKFLOW = ".github/workflows/canary.yml"
BRANCH = "refs/heads/main"
PREFIX = "plugin-lastfm-canary"
CREDENTIALS = ("LASTFM_API_KEY", "LASTFM_SHARED_SECRET")
# The build writes both names into the assembly only when it received the application.
APPLICATION = (b"JellySinLastfmApiKey", b"JellySinLastfmSharedSecret")
# An assembly version part holds at most 65534.
MAX_RUN = 65534
MAX_FILE_BYTES = 16 * 1024 * 1024
EPOCH = (1980, 1, 1, 0, 0, 0)
SECRET_LINE = re.compile(r"\s+(LASTFM_API_KEY|LASTFM_SHARED_SECRET): \$\{\{ secrets\.\1 \}\}")
FORBIDDEN = ("actions/publish", "actions/attest", "gh release", "git tag", "git push", "manifest", "set -x", "github.token", "GITHUB_TOKEN")


class CanaryError(ValueError):
    """The request or the build is not a canary of main, so nothing is named or packaged."""


def plan(ref, commit, run):
    """Return the names of one canary. Its version stays below every release: 0.0.<run>."""
    if ref != BRANCH:
        raise CanaryError(f"a canary is built from {BRANCH} only, not from {ref}")
    if not re.fullmatch(r"[0-9a-f]{40}", commit):
        raise CanaryError("the commit is not a full hash")
    if not re.fullmatch(r"[1-9][0-9]{0,4}", run) or int(run) > MAX_RUN:
        raise CanaryError(f"the run number must lie between 1 and {MAX_RUN}")
    return {"version": f"0.0.{run}-canary", "assembly-version": f"0.0.{run}.0", "name": f"{PREFIX}-0.0.{run}-{commit[:7]}"}


def require_credentials(environment):
    """Refuse a build that would carry no application. Nothing of either value is printed."""
    for name in CREDENTIALS:
        if not re.fullmatch(r"[a-fA-F0-9]{32}", environment.get(name, "")):
            raise CanaryError("the project-owned Last.fm application credentials are missing or malformed")


def read(path):
    if path.stat().st_size > MAX_FILE_BYTES:
        raise CanaryError(f"{path.name} is too large")
    return path.read_bytes()


def package(root, directory, name, version, output):
    """Write <name>.zip and checksums.txt and return (file, sha256) for the archive and the assembly."""
    if not re.fullmatch(re.escape(PREFIX) + r"-0\.0\.[1-9][0-9]{0,4}-[0-9a-f]{7}", name) or not re.fullmatch(r"0\.0\.[1-9][0-9]{0,4}-canary", version):
        raise CanaryError("the name or the version is not a canary name")
    descriptor = json.loads(read(Path(root) / DESCRIPTOR))
    assembly = descriptor["assemblyFile"]
    names = [assembly, *descriptor["packageFiles"]]
    if any(Path(entry).name != entry for entry in names):
        raise CanaryError(f"{DESCRIPTOR} names a packaged file outside the build output")
    files = {entry: read(Path(directory) / entry) for entry in names}
    if version.encode() not in files[assembly]:
        raise CanaryError(f"{assembly} does not carry the version {version}")
    if any(marker not in files[assembly] for marker in APPLICATION):
        raise CanaryError(f"{assembly} carries no Last.fm application")
    output = Path(output)
    output.mkdir(parents=True, exist_ok=False)
    with zipfile.ZipFile(output / f"{name}.zip", "x", zipfile.ZIP_STORED) as archive:
        for entry in names:
            info = zipfile.ZipInfo(entry, EPOCH)
            info.external_attr = 0o644 << 16
            archive.writestr(info, files[entry])
    sums = [(f"{name}.zip", hashlib.sha256(read(output / f"{name}.zip")).hexdigest()), (assembly, hashlib.sha256(files[assembly]).hexdigest())]
    (output / "checksums.txt").write_text("".join(f"{digest}  {file}\n" for file, digest in sums), encoding="utf-8")
    return sums


def block(lines, key):
    """Return the indented lines below one top-level key."""
    start = lines.index(key) + 1 if key in lines else len(lines)
    end = next((index for index in range(start, len(lines)) if lines[index] and not lines[index].startswith(" ")), len(lines))
    return [line for line in lines[start:end] if line.strip()]


def workflow_violations(text):
    """Return the limits the canary workflow text breaks: manual, main only, read-only, one day, no application in a script."""
    lines = text.splitlines()
    wrong = [] if block(lines, "on:") == ["  workflow_dispatch:"] else ["the only trigger is a manual dispatch without inputs"]
    if block(lines, "permissions:") != ["  contents: read"] or re.search(r":\s*write\b", text):
        wrong.append("the token reads contents and writes nothing")
    if re.findall(r"retention-days:\s*(\S+)", text) != ["1"]:
        wrong.append("the artefact is kept for one day")
    wrong += [f"the workflow does not name '{word}'" for word in FORBIDDEN if word in text]
    secret = [index for index, line in enumerate(lines) if "secrets." in line or "LASTFM_" in line]
    if not secret or any(not SECRET_LINE.fullmatch(lines[index]) for index in secret):
        wrong.append("the application reaches a step only as its two environment values")
    guard = [index for index, line in enumerate(lines) if "tools/canary.py plan --ref \"$GITHUB_REF\"" in line]
    if len(guard) != 1 or (secret and guard[0] > secret[0]):
        wrong.append("the ref is checked before a step receives the application")
    return wrong


def main(arguments=None):
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    named = commands.add_parser("plan", help="Refuse every ref but main and print the canary names as step outputs.")
    for option in ("--ref", "--commit", "--run"):
        named.add_argument(option, required=True)
    commands.add_parser("credentials", help="Refuse to continue without the application credentials.")
    packaged = commands.add_parser("package", help="Write the archive and print its checksums as a job summary.")
    for option in ("--directory", "--name", "--version", "--output"):
        packaged.add_argument(option, required=True)
    options = parser.parse_args(arguments)
    try:
        if options.command == "plan":
            print("".join(f"{key}={value}\n" for key, value in plan(options.ref, options.commit, options.run).items()), end="")
        elif options.command == "credentials":
            require_credentials(os.environ)
            print("The application credentials are present and well formed.")
        else:
            sums = package(Path("."), options.directory, options.name, options.version, options.output)
            print(f"### Canary {options.version}: not a release\n\n| File | SHA-256 |\n| --- | --- |")
            print("".join(f"| `{file}` | `{digest}` |\n" for file, digest in sums), end="")
    except (CanaryError, OSError, json.JSONDecodeError, KeyError, TypeError) as error:
        print(f"Canary refused: {error}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main())
