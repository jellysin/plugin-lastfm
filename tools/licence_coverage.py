"""Refuse files whose licence nobody declared on purpose.

REUSE.toml labels the whole tree "**" with EUPL-1.2, so `reuse lint` accepts any new file.
This check names where first-party files live and which types they have. A file passes
when it matches one of those places, one of the other REUSE.toml tables, or carries its
own SPDX licence header; anything else, such as a copied third-party asset,
fails until it is declared.

A file whose text names an MIT origin, such as an upstream MIT notice or a provenance
line that gives MIT in parentheses, must also be labelled MIT by its own SPDX header or
by a REUSE.toml table; the whole-tree EUPL-1.2 table alone would drop the upstream licence.
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
    "web/**.json", "web/**.ts",
)
# Split, so that reuse lint does not read this file as declaring a licence expression.
SPDX_HEADER = re.compile(rb"SPDX-License-" rb"Identifier:([^\r\n]*)")
# A notice is the provenance form, a licence title on a line of its own, or the grant text;
# prose that only mentions the MIT licence, such as a changelog entry, is not one.
MIT_NOTICE = re.compile(
    rb"\(MIT\)|^[ \t#/*<!-]*MIT Licen[cs]e[ \t]*$|Permission is hereby " rb"granted, free of charge", re.MULTILINE
)
MIT_ID = re.compile(r"\bMIT\b")
# REUSE ignores licence files wherever they are, so their MIT text needs no label.
LICENCE_FILE = re.compile(r"(?:^|.*/)(?:LICEN[CS]E|COPYING)[^/]*|LICENSES/.*")


class CoverageError(ValueError):
    """The file list or REUSE.toml could not be read, so nothing is concluded."""


def pattern(glob):
    """REUSE glob semantics: ** crosses directories, * and ? stay inside one."""
    parts = re.split(r"(\*\*|\*|\?)", glob)
    tokens = {"**": ".*", "*": "[^/]*", "?": "[^/]"}
    return re.compile("".join(tokens.get(part, re.escape(part)) for part in parts))


def declared_tables(reuse_toml):
    """(path, licence) of every REUSE.toml table path except the whole-tree "**" one."""
    try:
        data = tomllib.loads(reuse_toml.read_text(encoding="utf-8"))
    except (OSError, tomllib.TOMLDecodeError) as error:
        raise CoverageError(f"cannot read {reuse_toml}: {error}") from error
    declared = []
    for table in data.get("annotations", []):
        paths = table.get("path", [])
        licence = str(table.get("SPDX-License-Identifier", ""))
        declared += [(glob, licence) for glob in ([paths] if isinstance(paths, str) else paths)]
    return [(glob, licence) for glob, licence in declared if glob != "**"]


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


def head(path):
    """The first HEADER_BYTES of a file, or nothing when it cannot be read."""
    try:
        with path.open("rb") as handle:
            return handle.read(HEADER_BYTES)
    except OSError:
        return b""


def has_header(path):
    return bool(SPDX_HEADER.search(head(path)))


def undeclared(root):
    root = Path(root)
    tables = [glob for glob, _ in declared_tables(root / "REUSE.toml")]
    patterns = [pattern(glob) for glob in (*LICENCE_TEXTS, *FIRST_PARTY, *tables)]
    found = []
    for name in listed_files(root):
        if any(compiled.fullmatch(name) for compiled in patterns):
            continue
        if not (root / name).is_file() or not has_header(root / name):
            found.append(name)
    return found


def mit_unlabelled(root):
    """Files that name an MIT origin while neither a header nor a table labels them MIT."""
    root = Path(root)
    tables = declared_tables(root / "REUSE.toml")
    mit_tables = [pattern(glob) for glob, licence in tables if MIT_ID.search(licence)]
    found = []
    for name in listed_files(root):
        if LICENCE_FILE.fullmatch(name) or not (root / name).is_file():
            continue
        content = head(root / name)
        if not MIT_NOTICE.search(content):
            continue
        headers = [match.decode("utf-8", "replace") for match in SPDX_HEADER.findall(content)]
        if any(MIT_ID.search(expression) for expression in headers):
            continue
        if not any(compiled.fullmatch(name) for compiled in mit_tables):
            found.append(name)
    return found


def main(arguments=None):
    parser = argparse.ArgumentParser(description="Refuse files whose licence nobody declared on purpose.")
    parser.add_argument("--root", default=".", help="repository root (default: the current directory)")
    options = parser.parse_args(arguments)
    try:
        found = undeclared(options.root)
        unlabelled = mit_unlabelled(options.root)
    except CoverageError as error:
        print(f"licence coverage: {error}", file=sys.stderr)
        return 2
    for name in found:
        print(f"licence coverage: {name}: no declared place or type and no SPDX header; "
              "declare it in tools/licence_coverage.py or REUSE.toml, or add a header", file=sys.stderr)
    for name in unlabelled:
        print(f"licence coverage: {name}: names an MIT origin but is not labelled MIT; "
              "add a REUSE.toml table or an SPDX header that names MIT", file=sys.stderr)
    if found or unlabelled:
        return 1
    print("licence coverage: every file has a declared place and type, a REUSE.toml table or a header, "
          "and every file that names an MIT origin is labelled MIT")
    return 0


if __name__ == "__main__":
    sys.exit(main())
