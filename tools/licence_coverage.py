"""Refuse files whose licence nobody declared on purpose.

REUSE.toml labels the whole tree "**" with EUPL-1.2, so `reuse lint` accepts any new file.
This check names where first-party files live and which types they have. A file passes
when it matches one of those places, one of the other REUSE.toml tables, or carries its
own SPDX licence header; anything else, such as a copied third-party asset,
fails until it is declared.
"""

import argparse
import re
import subprocess
import sys
import tomllib
from pathlib import Path

LIST_TIMEOUT_SECONDS = 60
HEADER_BYTES = 4096
MAX_FILES = 20000
# REUSE ignores licence texts, so they never need a declaration.
LICENCE_TEXTS = ("LICENSE", "LICENSES/**")
FIRST_PARTY = (
    ".dockerignore", ".editorconfig", ".gitattributes", ".gitignore", ".gitleaksignore",
    ".mcp.json", ".node-version", ".python-version", ".release-please-manifest.json",
    ".standards-baseline.json", ".standards.lock", ".standards.yaml", "Directory.Build.props",
    "JellySin.Plugin.Lastfm.sln", "Makefile", "REUSE.toml", "eslint.config.mjs", "global.json",
    "lefthook.yml", "opencode.json", "package-lock.json", "package.json", "playwright.config.mjs",
    "plugin.json", "release-please-config.json", "renovate.json", "runtime-dependencies.json",
    "tsconfig.json", "version.txt",
    ".agents/**", ".claude/**", ".codex/**", ".config/**", ".continue/**", ".devcontainer/**",
    ".gemini/**", ".github/**", ".paperclip/**", ".vscode/**",
    "benchmarks/**.cs", "benchmarks/**.csproj", "benchmarks/**.json",
    "src/**.cs", "src/**.csproj", "src/**.css", "src/**.html", "src/**.json",
    "tests/**.cs", "tests/**.csproj", "tests/**.json", "tests/**.mjs", "tests/**.py",
    "tools/**.cs", "tools/**.csproj", "tools/**.json", "tools/**.mjs", "tools/**.py",
    "tools/**.sh", "tools/**.xml",
    "web/**.ts",
)
# Split, so that reuse lint does not read this file as declaring a licence expression.
SPDX_HEADER = re.compile(rb"SPDX-License-" rb"Identifier:")


class CoverageError(ValueError):
    """The file list or REUSE.toml could not be read, so nothing is concluded."""


def pattern(glob):
    """REUSE glob semantics: ** crosses directories, * and ? stay inside one."""
    parts = re.split(r"(\*\*|\*|\?)", glob)
    tokens = {"**": ".*", "*": "[^/]*", "?": "[^/]"}
    return re.compile("".join(tokens.get(part, re.escape(part)) for part in parts))


def declared_tables(reuse_toml):
    """Paths of every REUSE.toml table except the whole-tree "**" one."""
    try:
        data = tomllib.loads(reuse_toml.read_text(encoding="utf-8"))
    except (OSError, tomllib.TOMLDecodeError) as error:
        raise CoverageError(f"cannot read {reuse_toml}: {error}") from error
    globs = []
    for table in data.get("annotations", []):
        paths = table.get("path", [])
        globs += [paths] if isinstance(paths, str) else list(paths)
    return [glob for glob in globs if glob != "**"]


def listed_files(root):
    """Tracked files and untracked files git does not ignore."""
    command = ["git", "-C", str(root), "ls-files", "-z", "--cached", "--others", "--exclude-standard"]
    try:
        result = subprocess.run(command, capture_output=True, check=True, timeout=LIST_TIMEOUT_SECONDS)
    except (OSError, subprocess.SubprocessError) as error:
        raise CoverageError(f"cannot list files: {error}") from error
    files = sorted({name.decode("utf-8") for name in result.stdout.split(b"\0") if name})
    if len(files) > MAX_FILES:
        raise CoverageError(f"{len(files)} files exceed the bound of {MAX_FILES}")
    return files


def has_header(path):
    try:
        with path.open("rb") as handle:
            return bool(SPDX_HEADER.search(handle.read(HEADER_BYTES)))
    except OSError:
        return False


def undeclared(root):
    root = Path(root)
    globs = (*LICENCE_TEXTS, *FIRST_PARTY, *declared_tables(root / "REUSE.toml"))
    patterns = [pattern(glob) for glob in globs]
    found = []
    for name in listed_files(root):
        if any(compiled.fullmatch(name) for compiled in patterns):
            continue
        if not (root / name).is_file() or not has_header(root / name):
            found.append(name)
    return found


def main(arguments=None):
    parser = argparse.ArgumentParser(description="Refuse files whose licence nobody declared on purpose.")
    parser.add_argument("--root", default=".", help="repository root (default: the current directory)")
    options = parser.parse_args(arguments)
    try:
        found = undeclared(options.root)
    except CoverageError as error:
        print(f"licence coverage: {error}", file=sys.stderr)
        return 2
    for name in found:
        print(f"licence coverage: {name}: no declared place or type and no SPDX header; "
              "declare it in tools/licence_coverage.py or REUSE.toml, or add a header", file=sys.stderr)
    if found:
        return 1
    print("licence coverage: every file has a declared place and type, a REUSE.toml table or a header")
    return 0


if __name__ == "__main__":
    sys.exit(main())
