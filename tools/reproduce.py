#!/usr/bin/env python3
"""Build the same reviewed commit in two unrelated directories and compare release bytes."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tempfile


PROJECT = Path("src/JellySin.Plugin.Lastfm")


def run(arguments: list[str], directory: Path, timeout: int = 600) -> None:
    executable = shutil.which(arguments[0])
    if executable is None:
        raise RuntimeError(f"Required executable is unavailable: {arguments[0]}")
    result = subprocess.run([executable, *arguments[1:]], cwd=directory, timeout=timeout,
                            stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT, check=False)
    if result.returncode:
        # Compiler diagnostics can contain embedded application credentials.
        raise RuntimeError(f"Reproducibility stage {arguments[0]} failed (exit {result.returncode}).")


def git(root: Path, *arguments: str) -> str:
    result = subprocess.run(["git", *arguments], cwd=root, capture_output=True,
                            text=True, timeout=30, check=True)
    return result.stdout.strip()


def artifacts(directory: Path) -> dict[str, str]:
    files = sorted(directory.iterdir())
    names = {path.name for path in files}
    if (len(files) != 4 or not {"release.json", "sbom.spdx.json", "checksums.txt"} <= names
            or len([path for path in files if path.suffix == ".zip"]) != 1
            or any(path.is_symlink() or not path.is_file() or not 0 < path.stat().st_size <= 64_000_000 for path in files)):
        raise RuntimeError("Reproducibility build returned an unexpected artifact set.")
    return {path.name: hashlib.sha256(path.read_bytes()).hexdigest() for path in files}


def build(source: Path, commit: str, destination: Path, tooling: Path, tag: str | None) -> dict[str, str]:
    run(["git", "clone", "--quiet", "--no-checkout", "--no-hardlinks", str(source), str(destination)], source)
    run(["git", "checkout", "--quiet", "--detach", commit], destination)
    run(["git", "remote", "set-url", "origin", git(source, "remote", "get-url", "origin")], destination)
    run(["npm", "ci", "--ignore-scripts", "--no-audit", "--no-fund"], destination)
    run(["npm", "run", "build"], destination)
    project = str(PROJECT / "JellySin.Plugin.Lastfm.csproj")
    run(["dotnet", "restore", project, "--locked-mode"], destination)
    run(["dotnet", "build", project, "-c", "Release", "--no-restore", "-p:ContinuousIntegrationBuild=true"], destination)
    packaging = [sys.executable, str(tooling / "tools.py"), "package", "--publish-directory", str(PROJECT / "bin/Release/net10.0"),
                 "--nuget-lock-path", str(PROJECT / "packages.lock.json"),
                 "--nuget-assets-path", str(PROJECT / "obj/project.assets.json"),
                 "--runtime-dependencies-path", "runtime-dependencies.json"]
    if tag:
        packaging.extend(["--tag", tag])
    run(packaging, destination)
    return artifacts(destination / "dist")


def verify_results(first: dict[str, str], second: dict[str, str], reference: dict[str, str] | None) -> None:
    if first != second:
        raise RuntimeError("Exact-commit builds produced different release bytes across checkout paths.")
    if reference is not None and first != reference:
        raise RuntimeError("The artifacts selected for upload differ from the independent exact-commit rebuilds.")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tooling-directory", type=Path, required=True)
    parser.add_argument("--tag", help="Require both rebuilds to package this exact stable release tag.")
    parser.add_argument("--reference-directory", type=Path, help="Require rebuilds to match the actual artifacts selected for upload.")
    parser.add_argument("--report", type=Path, default=Path("build/reproducibility.json"))
    args = parser.parse_args()
    source = Path(git(Path.cwd(), "rev-parse", "--show-toplevel")).resolve()
    if git(source, "status", "--porcelain", "--untracked-files=no"):
        raise RuntimeError("Commit tracked changes before verifying exact-commit reproducibility.")
    tooling = args.tooling_directory.resolve(strict=True)
    if git(tooling, "status", "--porcelain", "--untracked-files=no"):
        raise RuntimeError("Commit packaging changes before verifying reproducibility.")
    commit = git(source, "rev-parse", "HEAD")
    if args.tag:
        if not re.fullmatch(r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", args.tag):
            raise RuntimeError("Reproducibility requires a stable vX.Y.Z release tag.")
        if git(source, "rev-parse", "--verify", f"refs/tags/{args.tag}^{{commit}}") != commit:
            raise RuntimeError("Reproducibility source does not match the exact release tag.")
    reference = artifacts(args.reference_directory.resolve(strict=True)) if args.reference_directory else None
    # TemporaryDirectory owns these exact newly-created paths and their cleanup.
    with tempfile.TemporaryDirectory(prefix="jellysin-reproduce-") as temporary:
        root = Path(temporary).resolve()
        first = build(source, commit, root / "first-checkout", tooling, args.tag)
        second = build(source, commit, root / "different" / "second-checkout", tooling, args.tag)
    verify_results(first, second, reference)
    # Catch changes to the selected upload set during the independent rebuilds as well.
    if args.reference_directory and artifacts(args.reference_directory.resolve(strict=True)) != reference:
        raise RuntimeError("Selected upload artifacts changed during the reproducibility check.")
    report = {"commit": commit, "tag": args.tag, "checkouts": 2, "independentRestores": True,
              "matchesSelectedArtifacts": reference is not None,
              "matchingArtifactSha256": first, "toolingCommit": git(tooling, "rev-parse", "HEAD")}
    args.report.parent.mkdir(parents=True, exist_ok=True)
    args.report.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print("Two independent exact-commit builds produced identical ZIP, metadata, SBOM and checksums.")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        raise SystemExit(str(error) if isinstance(error, RuntimeError) else "Reproducibility check failed: " + type(error).__name__) from None
