# Repository instructions

This is a Jellyfin plugin for Last.fm scrobbling, favourites, and music metadata.
Read [CONTRIBUTING.md](CONTRIBUTING.md) before changing release or branch behavior.

- Develop Jellyfin 12 on `main`; backport applicable fixes to `release/10.11`.
  Keep each branch's Jellyfin package and target framework compatible with its host.
- Preserve the plugin GUID, catalog URL, and existing XML configuration fields.
  Document any intentional behavior or configuration migration in the changelog.
- Use the SDK in `global.json`, exact package versions, and committed NuGet locks.
  Host-provided assemblies must not be shipped in the plugin ZIP.
- Test production behavior. Do not duplicate implementation logic in tests.
  Cover cancellation, user isolation, HTTP failures, and absent metadata.
- Pass cancellation through asynchronous work; contain exceptions at event boundaries.
  Keep work and caches bounded, and detach events before disposing services.
- Use HTTPS and structured logs. Never log credentials, session keys, signatures,
  authenticated URLs, or response bodies that could contain credentials.
- Enable nullable checks and analyzers. Fix warnings; a narrowly scoped suppression
  requires a concrete justification. Do not weaken a gate to make CI pass.
- Keep `.github/instructions` focused on facts not already explained here. Do not
  copy unrelated framework rules, invent licensing claims, or claim unrun checks.
- Use Conventional Commit PR titles and squash merges. Release-please owns versions
  and changelogs; `version.txt` is SemVer and Jellyfin versions append `.0`.
- Run formatting, build, tests, security, and package checks from CI before release.
  Report any unavailable validation explicitly.
- Use full commit SHAs for Actions, read-only workflow defaults, explicit timeouts,
  and job-scoped write permissions. Never execute untrusted PR code with write tokens.
- Build releases from their exact tag. Never replace a published artifact with
  different bytes. Catalog updates must preserve all published compatible releases.
- Required checks use `strict: false`; do not introduce a branch-up-to-date requirement.
- Verify live GitHub settings after changing them. Do not document planned settings
  as active, or assume bot-created events will trigger subsequent workflows.

The obsolete shared-core/Emby rewrite is not part of this plugin's architecture.
