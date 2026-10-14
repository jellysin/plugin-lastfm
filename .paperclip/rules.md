# Paperclip Operating Rules (jellysin/plugin-lastfm)

## Operating Contract

- Branch push != shipping. Open PR required. Work ships after merge.
- Rebase onto main immediately: run git fetch origin && git rebase origin/main
  before proposing.
- Rule 0 Terminal Disposition: every run ends with structured disposition:
  in_review or blocked.
- Ed25519 Exit-0 Receipts: none. .standards.yaml pins no valid
  receipt.public_key -> attach no receipt; pin key from `praetorctl gate keygen`
  to require receipts.
- Timeout != failure. Re-check open PRs before retry; prevent duplicate PRs.
- Text register internal: `caveman` skill: fragments, no filler, verbatim
  code/paths/errors; facts, paths, commands, verdict.

## Push Protocol

```bash
git push origin HEAD:refs/heads/paperclip/<issue-id>
```

## High-Integrity Invariants

- HISS-01: recursion prohibited; call graph = DAG
- HISS-02: scalar upper bound on every loop; explicit deadline on every I/O call
- HISS-04: McCabe cyclomatic <= 12, cognitive <= 15, statements <= 55; func
  LOC <= 60 (audit ceiling)
- HISS-07: every error handled or wrapped with context
- HISS-10: zero warnings: compiler, linter, format sweeps
- HISS-15: positive + negative + boundary tests, every public interface
- HISS-16: single canonical `AGENTS.md`; vendor files compiled via `praetorctl
  compile-context`
