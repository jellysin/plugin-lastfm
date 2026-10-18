#!/usr/bin/env bash
# Runs the REUSE lint that the required "REUSE lint" CI check runs
# (fsfe/reuse-action v6), then tools/licence_coverage.py, which refuses a file the
# whole-tree "**" table in REUSE.toml would license without anyone declaring it.
# A missing or older reuse fails instead of skipping.
set -euo pipefail

if ! command -v reuse >/dev/null 2>&1; then
  echo "reuse-lint: reuse not found; install reuse 6 (pipx install 'reuse>=6,<7')" >&2
  exit 1
fi
version="$(reuse --version)"
version="${version%%$'\n'*}"
case "${version}" in
  "reuse, version 6."*) ;;
  *)
    echo "reuse-lint: need reuse 6, found: ${version}" >&2
    exit 1
    ;;
esac
reuse lint
exec python3 tools/licence_coverage.py
