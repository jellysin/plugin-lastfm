"""Verify acceptance criteria table structure, evidence tokens and gap declarations."""

import argparse
import re
import sys
from pathlib import Path

ROWS = (
    "D1",
    "D2",
    "G1",
    "G2",
    "G4",
    "P1",
    "P2",
    "P3",
    "P4",
    "P5",
    "O1",
    "O2",
    "O3",
    "O4",
    "O5",
    "O6",
)

MAX_FILE_BYTES = 1024 * 1024
MAX_LINES = 10_000
MAX_ROWS = 200
MAX_TOKENS_PER_ROW = 50
# A recorded run is a link to one step of a GitHub Actions job of this repository.
RUN_LINK = re.compile(
    r"\[[^\]\n]+\]\(https://github\.com/jellysin/plugin-lastfm/actions/runs/\d+/job/\d+#step:\d+:1\)"
)
TOKEN = re.compile(r"`([^`]+)`")


def github_slug(heading: str) -> str:
    """Derive GitHub-compatible markdown heading slug."""
    text = heading.strip().rstrip("#").strip()
    slug = text.lower()
    slug = re.sub(r"[^\w\s-]", "", slug)
    return re.sub(r"\s+", "-", slug)


def extract_markdown_slugs(content: str) -> set[str]:
    """Collect heading slugs from markdown source text."""
    slugs = set()
    for line in content.splitlines()[:MAX_LINES]:
        stripped = line.strip()
        if stripped.startswith("#"):
            match = re.match(r"^#{1,6}\s+(.+)$", stripped)
            if match:
                slugs.add(github_slug(match.group(1)))
    return slugs


def parse_table_rows(text: str) -> list[tuple[str, str, str, str]]:
    """Parse table rows into (row_id, criterion, evidence, gap) tuples."""
    rows: list[tuple[str, str, str, str]] = []
    for line in text.splitlines()[:MAX_LINES]:
        stripped = line.strip()
        if not (stripped.startswith("|") and stripped.endswith("|")):
            continue
        cells = [c.strip() for c in stripped[1:-1].split("|")]
        if len(cells) != 4:
            continue
        if cells[0].lower() == "row" and cells[1].lower() == "criterion":
            continue
        if all(re.match(r"^:?-+:?$", c) for c in cells):
            continue
        rows.append((cells[0], cells[1], cells[2], cells[3]))
        if len(rows) > MAX_ROWS:
            break
    return rows


def check_row_structure(row_ids: list[str]) -> list[str]:
    """Check row presence, uniqueness, set identity and ordering."""
    errors: list[str] = []
    seen: set[str] = set()
    duplicates: list[str] = []
    for r in row_ids[:MAX_ROWS]:
        if r in seen and r not in duplicates:
            duplicates.append(r)
        seen.add(r)

    unknown = [r for r in row_ids[:MAX_ROWS] if r not in ROWS]
    missing = [r for r in ROWS if r not in seen]

    for u in unknown:
        errors.append(f"Unknown row: {u}")
    for d in duplicates:
        errors.append(f"Duplicate row: {d}")
    for m in missing:
        errors.append(f"Missing row: {m}")

    if not unknown and not duplicates and not missing:
        if tuple(row_ids) != ROWS:
            errors.append(f"Rows out of order: {', '.join(row_ids)}")
    return errors


def check_token(row_id: str, token: str, root: Path) -> list[str]:
    """Validate one backtick evidence token against disk files and anchors."""
    errors: list[str] = []
    if "::" in token and "#" in token:
        return [f"Row {row_id}: invalid evidence token '{token}'"]

    if "::" in token:
        path_str, name = token.split("::", 1)
        anchor = None
    elif "#" in token:
        path_str, anchor = token.split("#", 1)
        name = None
    else:
        path_str = token
        name = None
        anchor = None

    if not path_str:
        return [f"Row {row_id}: empty path in evidence token '{token}'"]
    if path_str.startswith("/") or Path(path_str).is_absolute():
        return [f"Row {row_id}: evidence path '{path_str}' must not be absolute"]
    if ".." in Path(path_str).parts:
        return [f"Row {row_id}: evidence path '{path_str}' must not contain '..'"]

    target_file = root / path_str
    try:
        target_file.resolve().relative_to(root.resolve())
    except ValueError:
        return [f"Row {row_id}: evidence path '{path_str}' escapes repository root"]

    if not target_file.is_file():
        return [f"Row {row_id}: evidence path does not exist: {path_str}"]

    try:
        content = target_file.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError) as exc:
        return [f"Row {row_id}: failed to read '{path_str}': {exc}"]

    if name is not None and name not in content:
        errors.append(f"Row {row_id}: symbol '{name}' not found in '{path_str}'")
    if anchor is not None:
        slugs = extract_markdown_slugs(content)
        if anchor not in slugs:
            errors.append(f"Row {row_id}: anchor '{anchor}' not found in '{path_str}'")
    return errors


def check(text: str, root: Path) -> list[str]:
    """Check acceptance criteria markdown text against repository root."""
    if len(text.encode("utf-8")) > MAX_FILE_BYTES:
        return ["File exceeds 1 MiB limit"]

    rows = parse_table_rows(text)
    if len(rows) > MAX_ROWS:
        return ["Table exceeds 200 rows limit"]

    errors: list[str] = []
    row_ids = [r[0] for r in rows]
    errors.extend(check_row_structure(row_ids))

    for row_id, _criterion, evidence_cell, gap_cell in rows[:MAX_ROWS]:
        raw_evidence = evidence_cell.strip()
        raw_gap = gap_cell.strip()

        if not raw_evidence:
            if not raw_gap:
                errors.append(f"Row {row_id}: empty Evidence must have a non-empty Gap")
            continue

        tokens = TOKEN.findall(raw_evidence)
        residue = RUN_LINK.sub("", TOKEN.sub("", raw_evidence)).replace(";", "").strip()
        if residue:
            errors.append(f"Row {row_id}: Evidence holds text that is neither a path nor a run link: {residue}")
            continue

        for token in tokens[:MAX_TOKENS_PER_ROW]:
            errors.extend(check_token(row_id, token, root))

    return errors


def main(argv: list[str] | None = None) -> int:
    """Execute CLI check for acceptance criteria document."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("file", nargs="?", default="docs/acceptance.md")
    parser.add_argument("--root", default=None)
    args = parser.parse_args(argv if argv is not None else sys.argv[1:])

    root = Path(args.root).resolve() if args.root else Path(__file__).resolve().parent.parent

    target_path = Path(args.file)
    if not target_path.is_absolute():
        target_path = root / target_path

    if not target_path.is_file():
        print(f"{args.file}: file not found", file=sys.stderr)
        return 1

    if target_path.stat().st_size > MAX_FILE_BYTES:
        print(f"{args.file}: file exceeds 1 MiB limit", file=sys.stderr)
        return 1

    try:
        text = target_path.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError) as exc:
        print(f"{args.file}: failed to read: {exc}", file=sys.stderr)
        return 1

    errors = check(text, root)
    if errors:
        for error in errors:
            print(f"{args.file}: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
