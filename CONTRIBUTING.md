# Contributing

Use the .NET SDK pinned in `global.json`, Python 3.13+, and Node.js 24 for the small
configuration-page test harness. A Jellyfin server is the plugin's runtime; the
DLL is not a standalone application.

## Branches and compatibility

`main` targets Jellyfin 12 and .NET 10. `release/10.11` maintains Jellyfin 10.11.11
on .NET 9. Start feature/fix branches from the appropriate remote branch. Shared
correctness and security fixes should be backported with their regression tests.
Do not merge the Jellyfin 12 package/framework bump into the maintenance branch.

## Local validation

```sh
dotnet restore --locked-mode
dotnet format --verify-no-changes --no-restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build --collect:"XPlat Code Coverage"
dotnet list package --vulnerable --include-transitive
python -m unittest discover -s tools -p "test_*.py"
node --test tests/config-page.test.cjs
```

When updating packages, restore without locked mode once, review and commit both
lockfiles, then repeat locked restore. Use exact stable versions appropriate to
the server runtime. Do not override the server's dependency graph indiscriminately.

PR titles use Conventional Commits (`fix:`, `feat:`, `chore:`, etc.). Explain the
resulting behavior and validation. Mark breaking changes with `!` and a migration
note. Required checks do not require rebasing onto the latest base branch.

## Releases

Release-please opens a version/changelog PR for each active branch. Merge that PR
to create a release. `version.txt` uses three-part SemVer; assembly, file, and
Jellyfin catalog versions append `.0`. The minimum Jellyfin ABI is separate.
For example, plugin `v12.0.1` installs as `12.0.1.0` with target ABI `12.0.0.0`.
Maintenance releases always increment the plugin patch version.

The release workflow validates the exact tag, creates the ZIP, publishes its
checksums and metadata, and reconciles the shared catalog through a checked PR.
It uses `GITHUB_TOKEN` and explicitly dispatches bot PR CI. Publication is chained
in the release workflow because token-created tags do not trigger another run.
For an interrupted publication, dispatch the release workflow with the existing
tag. Published versions are immutable; changed bytes require a new version.

All installation clients retain the same `main/manifest.json` URL. The catalog
contains both supported server lines and historical releases. Do not delete old
entries or manually change their checksums to repair an upload.

Keep GitHub settings aligned with the workflows: PRs and the `CI` check are
required with `strict: false`, squash merges are enabled, and workflow default
permissions are read-only. The combined Actions setting for creating/approving
PRs must remain enabled for release-please to create PRs; workflows do not approve
their own changes.
