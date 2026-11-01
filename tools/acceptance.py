"""Verify acceptance criteria table structure, evidence tokens and gap declarations."""

import argparse
import http.client
import json
import os
import re
import ssl
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
MAX_STEPS = 200

# A recorded run is a link to one step of a GitHub Actions job of this repository.
RUN_LINK = re.compile(
    r"\[[^\]\n]+\]\(https://github\.com/jellysin/plugin-lastfm/actions/runs/\d+/job/\d+#step:\d+:1\)"
)
RUN_LINK_URL = re.compile(
    r"https://github\.com/jellysin/plugin-lastfm/actions/runs/(?P<run_id>\d+)/job/(?P<job_id>\d+)#step:(?P<step>\d+):1"
)
TOKEN = re.compile(r"`([^`]+)`")


def github_slug(heading: str) -> str:
    """Derive GitHub-compatible markdown heading slug."""
    text = heading.strip().rstrip("#").strip()
    slug = text.lower()
    slug = re.sub(r"[^\w\s-]", "", slug)
    return re.sub(r"\s+", "-", slug)


def strip_fences_and_comments(text: str) -> str:
    """Remove fenced code blocks (``` and ~~~) and <!-- --> comments from markdown."""
    clean_text = re.sub(r"<!--[\s\S]*?-->", "", text)
    result_lines: list[str] = []
    in_fence: tuple[str, int] | None = None
    lines = clean_text.splitlines()
    for line in lines[:MAX_LINES]:
        stripped = line.strip()
        if in_fence is None:
            match = re.match(r"^(`{3,}|~{3,})", stripped)
            if match:
                chars = match.group(1)
                in_fence = (chars[0], len(chars))
                continue
            result_lines.append(line)
        else:
            match = re.match(r"^(`{3,}|~{3,})\s*$", stripped)
            if match and match.group(1)[0] == in_fence[0] and len(match.group(1)) >= in_fence[1]:
                in_fence = None
    return "\n".join(result_lines)


def extract_markdown_slugs(content: str) -> set[str]:
    """Collect heading slugs from markdown source with duplicate disambiguation."""
    stripped_content = strip_fences_and_comments(content)
    slugs: set[str] = set()
    counts: dict[str, int] = {}
    lines = stripped_content.splitlines()
    for line in lines[:MAX_LINES]:
        stripped = line.strip()
        if stripped.startswith("#"):
            match = re.match(r"^#{1,6}\s+(.+)$", stripped)
            if match:
                base = github_slug(match.group(1))
                count = counts.get(base, 0)
                slug = base if count == 0 else f"{base}-{count}"
                counts[base] = count + 1
                slugs.add(slug)
    return slugs


def parse_table_rows(text: str) -> list[tuple[str, str, str, str]]:
    """Parse table rows into (row_id, criterion, evidence, gap) tuples."""
    stripped_text = strip_fences_and_comments(text)
    rows: list[tuple[str, str, str, str]] = []
    lines = stripped_text.splitlines()
    for line in lines[:MAX_LINES]:
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
    missing = [r for r in ROWS[:MAX_ROWS] if r not in seen]

    for u in unknown[:MAX_ROWS]:
        errors.append(f"Unknown row: {u}")
    for d in duplicates[:MAX_ROWS]:
        errors.append(f"Duplicate row: {d}")
    for m in missing[:MAX_ROWS]:
        errors.append(f"Missing row: {m}")

    if not unknown and not duplicates and not missing and tuple(row_ids) != ROWS:
        errors.append(f"Rows out of order: {', '.join(row_ids)}")
    return errors


def check_symbol_declaration(row_id: str, path_str: str, name: str, content: str) -> list[str]:
    """Verify symbol declaration matches expected language syntax by file extension."""
    if not name:
        return [f"Row {row_id}: empty test name in evidence token '{path_str}::{name}'"]

    suffix = Path(path_str).suffix.lower()
    escaped = re.escape(name)
    if suffix == ".cs":
        pattern = rf"\b(?:void|Task)\s+{escaped}\s*\("
        if not re.search(pattern, content):
            return [f"Row {row_id}: symbol '{name}' not found in '{path_str}'"]
    elif suffix == ".py":
        pattern = rf"^\s*(?:async\s+)?def\s+{escaped}\s*\("
        if not re.search(pattern, content, re.MULTILINE):
            return [f"Row {row_id}: symbol '{name}' not found in '{path_str}'"]
    elif suffix in {".mjs", ".js", ".ts"}:
        pattern = rf"\btest\(\s*(['\"`]){escaped}\1"
        if not re.search(pattern, content):
            return [f"Row {row_id}: symbol '{name}' not found in '{path_str}'"]
    else:
        return [f"Row {row_id}: test names are only checked in .cs, .py, .mjs, .js, .ts files"]
    return []


class HTTPError(Exception):
    """HTTP response error holding status code and reason."""

    def __init__(self, code: int, reason: str) -> None:
        super().__init__(f"{code} {reason}")
        self.code = code
        self.reason = reason


def parse_token(row_id: str, token: str) -> tuple[tuple[str, str | None, str | None] | None, str | None]:
    """Parse evidence token into (path, name, anchor) or return validation error."""
    if "::" in token and "#" in token:
        return None, f"Row {row_id}: invalid evidence token '{token}'"
    if "::" in token:
        path_str, name = token.split("::", 1)
        anchor = None
    elif "#" in token:
        path_str, anchor = token.split("#", 1)
        name = None
    else:
        path_str, name, anchor = token, None, None
    if not path_str:
        return None, f"Row {row_id}: empty path in evidence token '{token}'"
    return (path_str, name, anchor), None


def validate_path(row_id: str, path_str: str, root: Path) -> tuple[Path | None, str | None]:
    """Validate evidence path bounds and existence relative to repository root."""
    if path_str.startswith("/") or Path(path_str).is_absolute():
        return None, f"Row {row_id}: evidence path '{path_str}' must not be absolute"
    if ".." in Path(path_str).parts:
        return None, f"Row {row_id}: evidence path '{path_str}' must not contain '..'"
    target_file = root / path_str
    try:
        target_file.resolve().relative_to(root.resolve())
    except ValueError:
        return None, f"Row {row_id}: evidence path '{path_str}' escapes repository root"
    if not target_file.is_file():
        return None, f"Row {row_id}: evidence path does not exist: {path_str}"
    return target_file, None


def read_target_content(row_id: str, path_str: str, target_file: Path) -> tuple[str | None, str | None]:
    """Read target file content with line limit check."""
    try:
        content = target_file.read_text(encoding="utf-8")
    except (OSError, UnicodeDecodeError) as exc:
        return None, f"Row {row_id}: failed to read '{path_str}': {exc}"
    if len(content.splitlines()) > MAX_LINES:
        return None, f"Row {row_id}: target file '{path_str}' exceeds {MAX_LINES} lines limit"
    return content, None


def check_token(row_id: str, token: str, root: Path) -> list[str]:
    """Validate one backtick evidence token against disk files, anchors and declarations."""
    parsed, parse_err = parse_token(row_id, token)
    if parse_err is not None or parsed is None:
        return [parse_err] if parse_err else []
    path_str, name, anchor = parsed

    target_file, path_err = validate_path(row_id, path_str, root)
    if path_err is not None or target_file is None:
        return [path_err] if path_err else []

    content, read_err = read_target_content(row_id, path_str, target_file)
    if read_err is not None or content is None:
        return [read_err] if read_err else []

    errors: list[str] = []
    if name is not None:
        errors.extend(check_symbol_declaration(row_id, path_str, name, content))
    if anchor is not None:
        slugs = extract_markdown_slugs(content)
        if anchor not in slugs:
            errors.append(f"Row {row_id}: anchor '{anchor}' not found in '{path_str}'")
    return errors


def fetch_job_data(job_id: str, job_cache: dict[str, dict]) -> dict:
    """Fetch GitHub Actions job metadata from API with in-memory caching."""
    if job_id in job_cache:
        return job_cache[job_id]

    headers = {
        "Accept": "application/vnd.github+json",
        "User-Agent": "plugin-lastfm-acceptance-checker",
    }
    token = os.environ.get("GITHUB_TOKEN", "").strip()
    if token:
        headers["Authorization"] = f"Bearer {token}"

    # Fixed host, verified TLS context; the audit rule flags every HTTPSConnection
    # because pre-3.4.3 Python skipped certificate checks, which 3.14+ does not.
    conn = http.client.HTTPSConnection(  # nosemgrep: python.lang.security.audit.httpsconnection-detected.httpsconnection-detected
        "api.github.com", timeout=15, context=ssl.create_default_context()
    )
    try:
        conn.request("GET", f"/repos/jellysin/plugin-lastfm/actions/jobs/{job_id}", headers=headers)
        resp = conn.getresponse()
        if resp.status != 200:
            raise HTTPError(resp.status, resp.reason)
        data = json.loads(resp.read(MAX_FILE_BYTES).decode("utf-8"))
        if not isinstance(data, dict):
            raise TypeError
        job_cache[job_id] = data
        return data
    finally:
        conn.close()


def check_run_link(row_id: str, link_url: str, job_cache: dict[str, dict]) -> list[str]:
    """Validate a single GitHub Actions run link via API."""
    match = RUN_LINK_URL.search(link_url)
    if not match:
        return [f"Row {row_id}: invalid run link '{link_url}'"]

    expected_run = match.group("run_id")
    job_id = match.group("job_id")
    expected_step = match.group("step")

    try:
        data = fetch_job_data(job_id, job_cache)
    except HTTPError as exc:
        return [f"Row {row_id}: link '{link_url}' HTTP error: {exc.code} {exc.reason}"]
    except (http.client.HTTPException, TimeoutError, OSError, ValueError, TypeError) as exc:
        return [f"Row {row_id}: link '{link_url}' network error: {type(exc).__name__}"]

    if str(data.get("run_id")) != expected_run:
        return [f"Row {row_id}: link '{link_url}' run_id mismatch: expected {expected_run}, got {data.get('run_id')}"]

    if data.get("conclusion") != "success":
        return [f"Row {row_id}: link '{link_url}' job conclusion is '{data.get('conclusion')}', expected 'success'"]

    steps = data.get("steps", [])
    step_match = None
    for step in steps[:MAX_STEPS]:
        if str(step.get("number")) == expected_step:
            step_match = step
            break

    if step_match is None:
        return [f"Row {row_id}: link '{link_url}' step {expected_step} not found"]
    if step_match.get("conclusion") != "success":
        return [f"Row {row_id}: link '{link_url}' step {expected_step} conclusion is '{step_match.get('conclusion')}', expected 'success'"]

    return []


def check_row_evidence(
    row_id: str,
    raw_evidence: str,
    raw_gap: str,
    root: Path,
    check_links: bool,
    job_cache: dict[str, dict],
) -> list[str]:
    """Validate evidence cell tokens and run links for a single row."""
    clean_evidence = raw_evidence.replace(";", "").strip()
    if not clean_evidence:
        if not raw_gap:
            return [f"Row {row_id}: needs evidence or a gap"]
        return []

    tokens = TOKEN.findall(raw_evidence)
    residue = RUN_LINK.sub("", TOKEN.sub("", raw_evidence)).replace(";", "").strip()
    if residue:
        return [f"Row {row_id}: Evidence holds text that is neither a path nor a run link: {residue}"]

    if len(tokens) > MAX_TOKENS_PER_ROW:
        return [f"Row {row_id}: evidence tokens count exceeds limit of {MAX_TOKENS_PER_ROW}"]

    errors: list[str] = []
    for token in tokens[:MAX_TOKENS_PER_ROW]:
        errors.extend(check_token(row_id, token, root))

    if check_links:
        run_matches = list(RUN_LINK_URL.finditer(raw_evidence))[:MAX_TOKENS_PER_ROW]
        for match in run_matches:
            errors.extend(check_run_link(row_id, match.group(0), job_cache))

    return errors


def check(text: str, root: Path, check_links: bool = False) -> list[str]:
    """Check acceptance criteria markdown text against repository root."""
    if len(text.encode("utf-8")) > MAX_FILE_BYTES:
        return ["File exceeds 1 MiB limit"]

    lines = text.splitlines()
    if len(lines) > MAX_LINES:
        return [f"File exceeds {MAX_LINES} lines limit"]

    rows = parse_table_rows(text)
    if len(rows) > MAX_ROWS:
        return ["Table exceeds 200 rows limit"]

    errors: list[str] = []
    row_ids = [r[0] for r in rows]
    errors.extend(check_row_structure(row_ids))

    job_cache: dict[str, dict] = {}
    for row_id, criterion, evidence_cell, gap_cell in rows[:MAX_ROWS]:
        raw_criterion = criterion.strip()
        if not raw_criterion:
            errors.append(f"Row {row_id}: Criterion must not be empty")

        errors.extend(
            check_row_evidence(
                row_id,
                evidence_cell.strip(),
                gap_cell.strip(),
                root,
                check_links,
                job_cache,
            )
        )

    return errors


def main(argv: list[str] | None = None) -> int:
    """Execute CLI check for acceptance criteria document."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("file", nargs="?", default="docs/acceptance.md")
    parser.add_argument("--root", default=None)
    parser.add_argument("--check-links", action="store_true", default=False)
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

    errors = check(text, root, check_links=args.check_links)
    if errors:
        for error in errors[:MAX_LINES]:
            print(f"{args.file}: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
