#!/usr/bin/env python3
"""Run a planted check, passing only when it exits 1 with expected failure text in its report."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import subprocess
import sys
from typing import Sequence

DEFAULT_TIMEOUT = 1800


def parse_args(argv: Sequence[str]) -> tuple[argparse.Namespace, list[str]]:
    args_list = list(argv)
    if "--" in args_list:
        separator = args_list.index("--")
        tool_args = args_list[:separator]
        command = args_list[separator + 1:]
    else:
        tool_args = args_list
        command = []

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--report", type=Path, required=True, help="Path to report JSON.")
    parser.add_argument("--expect", type=str, required=True, help="Substring expected in report failure text.")
    parser.add_argument("--timeout", type=int, default=DEFAULT_TIMEOUT, help="Subprocess timeout in seconds.")
    options = parser.parse_args(tool_args)
    return options, command


def run_check(report_path: Path, expect: str, command: list[str], timeout: int) -> int:
    if not expect:
        print("FAIL: Expected failure substring must not be empty.", file=sys.stderr)
        return 1
    if not command:
        print("FAIL: No command provided to execute.", file=sys.stderr)
        return 1

    if report_path.exists():
        report_path.unlink()

    try:
        proc = subprocess.run(command, timeout=timeout, check=False)
    except subprocess.TimeoutExpired:
        print(f"FAIL: Command timed out after {timeout} seconds: {command}", file=sys.stderr)
        return 1

    if proc.returncode != 1:
        print(f"FAIL: Command exited with code {proc.returncode}, expected 1.", file=sys.stderr)
        return 1

    if not report_path.is_file():
        print(f"FAIL: Report file {report_path} was not created by the command.", file=sys.stderr)
        return 1

    try:
        data = json.loads(report_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        print(f"FAIL: Failed to read or parse report {report_path}: {error}", file=sys.stderr)
        return 1

    if not isinstance(data, dict):
        print(f"FAIL: Report {report_path} is not a JSON object.", file=sys.stderr)
        return 1

    if data.get("passed"):
        print(f"FAIL: Report indicates passed={data.get('passed')!r}, expected refusal.", file=sys.stderr)
        return 1

    failure_text = data.get("failure")
    if not isinstance(failure_text, str) or expect not in failure_text:
        print(f"FAIL: Report failure {failure_text!r} does not contain expected substring {expect!r}.", file=sys.stderr)
        return 1

    print(f"PASS: Command failed with rc=1 and failure contains {expect!r}.", flush=True)
    return 0


def main(argv: Sequence[str] | None = None) -> int:
    if argv is None:
        argv = sys.argv[1:]
    try:
        options, command = parse_args(argv)
    except SystemExit as exc:
        return exc.code if isinstance(exc.code, int) else 1
    return run_check(options.report, options.expect, command, options.timeout)


if __name__ == "__main__":
    raise SystemExit(main())
