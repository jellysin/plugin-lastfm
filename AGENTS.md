# Repository instructions

Read CONTRIBUTING.md before changing release or branch behavior.

- This is a fresh Jellyfin 12 implementation. Do not add 10.11 or Emby compatibility.
- Preserve the new plugin GUID and stable catalog URL; document intentional migrations.
- Use global.json, exact package versions and committed NuGet locks. Never ship host assemblies.
- Compile the web resources before .NET. Keep frontend implementation details out of user flows.
- Test production behavior: cancellation, user isolation, failures, absent metadata and restart recovery.
- Bound queues, caches, requests, retries, pagination and retained state. Pass cancellation through asynchronous work.
- Keep playback callbacks free of network, database and disk operations; measure performance changes.
- Resolve the caller from authenticated identity, never an arbitrary submitted user ID.
- Confirm destructive favourite/history actions with a concrete preview and revalidate before applying.
- Use HTTPS and structured logs. Never log credentials, session keys, signatures or authenticated URLs.
- Store account secrets through server-side protection and keep Last.fm session keys out of browser responses.
- Fix nullable/compiler/analyzer/linter findings. Narrow suppressions require a concrete justification.
- Keep functions focused: backend at most 120 lines/60 statements/cognitive complexity 30, frontend at most 100 lines.
- Conventional Commit PR titles, squash merges, release-please-owned SemVer, separate host ABI.
- Full-SHA Actions, read-only workflow defaults, job-scoped writes, explicit timeouts; no untrusted PR code with write tokens.
- Build releases from exact tags, require project application credentials, attest all artifacts, preserve published bytes.
- Required CI uses strict: false. Verify live settings and explicitly dispatch bot checks.
- Do not claim unrun validation, guaranteed exactly-once scrobbles, invented speedups, or exclusive copyright ownership.
- Follow applicable JellySin engineering principles; do not copy unrelated framework-specific rules.
