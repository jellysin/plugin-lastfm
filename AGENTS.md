<!-- markdownlint-disable MD013 -->
# jellysin/plugin-lastfm Agent Operating Harness

<!-- praetor:head -->

Before concluding any turn:

```bash
make verify-all
```

`make verify-all` = repository gate. Steps live in `Makefile`: read there, never restate; adoption executed none.
Pass = exit 0. Signed Ed25519 Exit-0 receipt only from `praetorctl gate run`; report no receipt it did not mint. Fail -> SARIF diagnostic distillation (<= 1500 tokens).

## Core Directives & Invariants

Adopted check = check adoption generated here (generated `make verify-all` + praetor `lefthook.yml`), as written at adoption; later edits to those files not reflected. `not enforced` = rule binds, no generated check decides it for repository languages.

| Invariant | Rule | Adopted check | On fail |
| :--- | :--- | :--- | :--- |
| **HISS-01** control flow | recursion prohibited; call graph = DAG | `praetorctl audit` HISS scan in verify-all + lefthook pre-commit/pre-push: Go `goto`, recursion + plain-function call cycles; Rust, Python direct recursion; C `goto` | new finding fails verify-all, blocks commit, blocks push |
| **HISS-02** loops, I/O | scalar upper bound on every loop; explicit deadline on every I/O call | `praetorctl audit` HISS scan in verify-all + lefthook pre-commit/pre-push: unbounded loop shapes in Go, C, Rust, Python; I/O deadlines unchecked | new finding fails verify-all, blocks commit, blocks push |
| **HISS-03** memory | n/a: no such construct in repository languages; advisory | not enforced | advisory |
| **HISS-04** complexity | McCabe cyclomatic <= 12, cognitive <= 15, statements <= 55; func LOC <= 60 (audit ceiling) | `praetorctl audit` HISS scan in verify-all + lefthook pre-commit/pre-push: function length only; other caps need repository linter | new finding fails verify-all, blocks commit, blocks push |
| **HISS-05** scoping | declare every identifier in smallest lexical scope serving it | not enforced | advisory |
| **HISS-06** concurrency | explicit scalar upper bound on every worker pool + concurrent fan-out | not enforced | advisory |
| **HISS-07** errors | every error handled or wrapped with context | `praetorctl audit` HISS scan in verify-all + lefthook pre-commit/pre-push: partial in Go, Rust, Python | new finding fails verify-all, blocks commit, blocks push |
| **HISS-08** determinism | zero dynamic code execution (`eval` / `exec`) | `praetorctl audit` HISS scan in verify-all + lefthook pre-commit/pre-push: C banned calls, Python `eval` / `exec`; Go unchecked | new finding fails verify-all, blocks commit, blocks push |
| **HISS-09** reference safety | n/a: no such construct in repository languages; advisory | not enforced | advisory |
| **HISS-10** warnings | zero warnings: compiler, linter, format sweeps | not enforced | advisory |
| **HISS-11** supply chain | pinned lockfiles; zero floating tags; signed provenance | not enforced | advisory |
| **HISS-12** secrets | zero credentials in Git history | not enforced | advisory |
| **HISS-13** debt ratchet | recorded infractions never grow vs committed baseline | `praetorctl audit` ratchet vs `.standards-baseline.json` in verify-all + lefthook pre-commit/pre-push | growth fails verify-all, blocks commit, blocks push |
| **HISS-14** append-only ABI | public API append-only; breaking change = `!` subject + `Migration:` footer | not enforced | advisory |
| **HISS-15** 3D testing | positive + negative + boundary tests, every public interface | not enforced | advisory |
| **HISS-16** context integrity | single canonical `AGENTS.md`; vendor files compiled via `praetorctl compile-context` | `praetorctl compile-context --verify` in verify-all + lefthook pre-commit | drift fails verify-all, blocks commit |
| **HISS-17** state ledger | turn start `praetorctl state status`; turn end `praetorctl state sync .` | `praetorctl state sync .` in lefthook post-commit | none; records only |
| **HISS-18** CI efficiency | diff-aware gating via `praetorctl ci filter` | not enforced | advisory |
| **HISS-19** reuse before writing | one behavior = one implementation; extend or call existing code | not enforced | advisory |
| **HISS-20** replayable evidence | every enforcement claim backed by fixtures replayed both directions | not enforced | advisory |
| **HISS-21** platform neutrality | gates, hooks, emitted templates run on Linux, macOS, Windows, or skip with stated reason | not enforced | advisory |

## Operational Rules

1. **Act on verified state.** Read source files, run real commands before hypothesis or edit. Never guess flag names, library signatures, repo configuration from memory.

2. **Lead with output.** Direct answers, diffs, commands. No filler preamble, no "Based on", no restatement, no chatter.

3. **Context transpiler first.** Never edit `CLAUDE.md`, `.cursor/rules/hiss-invariants.mdc`, `.github/copilot-instructions.md`, `.windsurfrules`, `.gemini/GEMINI.md`, `.codex/rules.md` manually. All agent instruction updates -> `AGENTS.md`, then:

   ```bash
   praetorctl compile-context
   ```

   - `AGENTS.md` = agent-only text -> caveman (internal register). `praetorctl compile-context --verify` + `praetorctl audit` run caveman lint; findings fail gate; no opt-out. Check first: `praetorctl caveman check --kind=context AGENTS.md`.

4. **SARIF diagnostic distillation.** Compiler/linter errors -> distill to $\le 1,500$ tokens ($< 60$ lines): top 3 root-cause failures with file/line pointers; full SARIF logs -> ephemeral storage.

5. **No evasion.** Never attempt `--no-verify`, `LEFTHOOK=0`, or modifying `.git/hooks`. Hooks = local gate adoption installs. Adoption scaffolds no CI workflow: `praetorctl` gates run local only.

6. **Anti-loop interception.** Same AST diff + error category repeats $\ge 3$ times -> halt immediately. Re-evaluate design; no micro-textual retries.

<!-- praetor:config -->

## Text Register

<!-- praetor:register:start -->
Register follows the audience, then the task label of your brief (`register:` in `.standards.yaml`; labels are the router's `target_tasks`).

| Register | Where | Form |
| :--- | :--- | :--- |
| social | forge: issues, PR bodies, review comments, commit bodies | `social-text` skill: BLUF, full sentences, scannable, enough and no more; conventional commit subject unchanged |
| docs | docs/, README, ADR bodies | complete without bloat: newcomer path first, expert reference after; every claim points at a file, command or test; no restated code |
| internal | briefs, agent-to-agent traffic, research fan-outs, workflow returns | `caveman` skill: fragments, no filler, verbatim code/paths/errors; facts, paths, commands, verdict |

- Task rows: social = commit_message_synthesis, waiver_signoff; docs = architecture_synthesis, function_docstrings; every other label and any unlabeled text = internal. Subagent launch brief: `caveman` brief shape with `task:` = routing label.
- Evidence above 58 lines or 1500 tokens leaves the message as a file under `.workingdir/evidence/`; return `evidence: <path> sha256:<12 hex> lines:<n>` and fetch it only when a decision needs it.
- An internal return carries verdict, changed paths, commands run, evidence pointers and open questions, nothing else.
<!-- praetor:register:end -->

<!-- praetor:tail -->

## Primary Verification Commands

```bash
# Fast local test suite
# Declared commands only; run them before claiming application verification.
'npm' 'run' 'build'
'dotnet' 'restore' './benchmarks/JellySin.Plugin.Lastfm.Benchmarks/JellySin.Plugin.Lastfm.Benchmarks.csproj' '--locked-mode'
'dotnet' 'build' './benchmarks/JellySin.Plugin.Lastfm.Benchmarks/JellySin.Plugin.Lastfm.Benchmarks.csproj' '-c' 'Release' '--no-restore' '-warnaserror'
'dotnet' 'restore' './src/JellySin.Plugin.Lastfm/JellySin.Plugin.Lastfm.csproj' '--locked-mode'
'dotnet' 'build' './src/JellySin.Plugin.Lastfm/JellySin.Plugin.Lastfm.csproj' '-c' 'Release' '--no-restore' '-warnaserror'
'dotnet' 'restore' './tests/JellySin.Plugin.Lastfm.Tests/JellySin.Plugin.Lastfm.Tests.csproj' '--locked-mode'
'dotnet' 'build' './tests/JellySin.Plugin.Lastfm.Tests/JellySin.Plugin.Lastfm.Tests.csproj' '-c' 'Release' '--no-restore' '-warnaserror'
'dotnet' 'restore' './tests/integration/JellySin.NativeProbe/JellySin.NativeProbe.csproj' '--locked-mode'
'dotnet' 'build' './tests/integration/JellySin.NativeProbe/JellySin.NativeProbe.csproj' '-c' 'Release' '--no-restore' '-warnaserror'
'dotnet' 'restore' './tools/JellySin.CodePolicy/JellySin.CodePolicy.csproj' '--locked-mode'
'dotnet' 'build' './tools/JellySin.CodePolicy/JellySin.CodePolicy.csproj' '-c' 'Release' '--no-restore' '-warnaserror'
'npm' 'run' 'check'
'npm' 'run' 'test'
'dotnet' 'test' './tests/JellySin.Plugin.Lastfm.Tests/JellySin.Plugin.Lastfm.Tests.csproj' '-c' 'Release' '--no-build'
'python3' '-c' 'import sys; sys.exit(0 if sys.version_info >= (3, 14) else '\''Python 3.14+ is required for zero-test failure detection'\'')'
'python3' '-m' 'unittest' 'discover' '-s' 'tests/python'

# Recompile and verify cross-agent context outputs
praetorctl compile-context --verify

# Audit repository against declared HISS standards
praetorctl audit

# Repository gate; steps live in Makefile
make verify-all
```

<!-- markdownlint-enable MD013 -->
<!-- markdownlint-disable MD025 -->
<!-- praetor:harness:end -->

---

# Repository instructions

Read CONTRIBUTING.md before changing release or branch behavior.

- Fresh Jellyfin 12 implementation. No 10.11 or Emby compatibility.
- Preserve new plugin GUID and Plugin Repository URL; document intentional migrations.
- Use global.json, exact package versions and committed NuGet locks. Never ship host assemblies.
- Compile web resources before .NET. Keep frontend implementation details out of user flows.
- Test production behavior: cancellation, user isolation, failures, absent metadata and restart recovery.
- Bound queues, caches, requests, retries, pagination and retained state. Pass cancellation through asynchronous work.
- Keep playback callbacks free of network, database and disk operations; measure performance changes.
- Resolve caller from authenticated identity, never from arbitrary submitted user ID.
- Confirm destructive favourite/history actions with concrete preview; revalidate before applying.
- Use HTTPS and structured logs. Never log credentials, session keys, signatures or authenticated URLs.
- Store account secrets through server-side protection and keep Last.fm session keys out of browser responses.
- Fix nullable/compiler/analyzer/linter findings. Narrow suppressions require concrete justification.
- Keep functions focused: backend at most 120 lines/60 statements/cognitive complexity 30, frontend at most 100 lines.
- Conventional Commit PR titles, squash merges, release-please-owned SemVer, separate host ABI.
- Release-please only collects version and changelog changes in PR. Never create tags or publish releases from ordinary pushes or PR merges.
- Publication stays separate, explicitly requested maintainer action on exact version tag.
- Full-SHA Actions, read-only workflow defaults, job-scoped writes, explicit timeouts; no untrusted PR code with write tokens.
- Build releases from exact tags, require project application credentials, attest all artifacts, preserve published bytes.
- Required CI uses strict: false. Verify live settings and explicitly dispatch bot checks.
- Do not claim unrun validation, guaranteed exactly-once scrobbles, invented speedups, or exclusive copyright ownership.
- Follow applicable JellySin engineering principles; do not copy unrelated framework-specific rules.
