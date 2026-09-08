# Contributing

Use a feature branch from main and a Conventional Commit PR title. Squash merging
keeps reviewed changes attributable in release notes. Required checks use
`strict: false`; there is no branch-up-to-date requirement. This repository targets
Jellyfin 12 only, using .NET SDK 10.0.400 and committed exact NuGet locks.

## Build and validate

Use Node 24.20.0 and Python 3.14.7 for the web build and validation helpers. Web
JavaScript is generated into the plugin's embedded-resource directory and is not
committed. Build it before compiling .NET:

```sh
npm ci --ignore-scripts
npm run build
npm run check
npm test
npx --no-install playwright install chromium
npm run test:e2e
dotnet restore --locked-mode
dotnet format --verify-no-changes --no-restore
dotnet build -c Release --no-restore
dotnet run --project tools/JellySin.CodePolicy -c Release --no-build -- --self-test
dotnet run --project tools/JellySin.CodePolicy -c Release --no-build -- src
dotnet test tests/JellySin.Plugin.Lastfm.Tests/JellySin.Plugin.Lastfm.Tests.csproj -c Release --no-build --collect:"XPlat Code Coverage" --results-directory build/coverage
python tools/coverage.py build/coverage
python -m unittest discover -s tests/python
```

CI also audits npm/NuGet dependencies, checks repository policy and Action pins,
runs actionlint and Gitleaks, packages the DLL, and exercises a real Jellyfin 12
container through `tools/smoke.py`. CodeQL scans source and Actions separately.
Report unavailable checks and remaining limitations rather than claiming they ran.

Tests should cover observed behavior, cancellation, isolation, absent metadata,
HTTP failures, restart recovery and user-confirmed mutations. Coverage gates are
70% overall and 85% for security-sensitive code. Performance changes need a
repeatable workload and measurements; see benchmarks/README.md.
The C# source policy enforces 120 function lines, 60 AST statements and cognitive
complexity 30 using Roslyn and the official Sonar S3776 analyzer; its self-test
checks the gate itself. CI retains the production callback benchmark's JSON output.
The coverage gate selects one newest Cobertura run and deduplicates physical source
lines across nested/generated classes. It measures the production assembly only,
requires 70% overall and 85% combined API/configuration/transport line coverage,
and never merges old runs or trusts summary percentages from the report header.

## Release and compatibility

Release-please collects commits in a release PR and updates `version.txt`, web
package versions and the changelog. Its `skip-github-release: true` setting prevents
tag and release creation. Ordinary pushes and PR merges never publish a release.
The first release PR targets `1.0.0`; it stays open while changes accumulate.

Stable `vX.Y.Z` tags map to `X.Y.Z.0` for the assembly and Plugin Repository;
the minimum Jellyfin ABI is separately recorded in `plugin.json`.
Do not ship host-provided assemblies.

A maintainer separately decides when to publish. After reviewing and merging the
release PR, create the chosen version tag and an unpublished draft, then manually
dispatch `release.yml` **on that tag ref**. This binds GitHub provenance to the
source tag. The publication workflow has no push or PR trigger and is never
dispatched by release-please. It reruns CI and builds production bytes. Packaging verifies the NuGet
production graph against the committed lock and restored assets, then creates a
deterministic ZIP, SPDX file/dependency inventory and checksums. Independent builds
in different checkout paths must produce the same four artifacts. The workflow
attests them, verifies existing draft bytes and publishes only a complete release.
Immutable releases must be
enabled before publication. Retry by dispatching the same workflow on the same tag;
never use an upload replacement option or change already published bytes.

The plugin is in development and has no published release available.
Draft asset checks use authenticated numeric API identities; GitHub assigns their
canonical public download URLs when the release is published.

Production builds require the project-owned `LASTFM_API_KEY` and
`LASTFM_SHARED_SECRET` repository secrets. CI and fork PR builds do not receive them.
Release publication fails if they are absent or malformed. They become assembly
metadata in distributed plugin bytes and are extractable by server owners; build
secret storage does not turn a distributed desktop application secret into a
confidential server credential.

Only the built-in repository token is used. Bot PR CI is explicitly dispatched;
publication requires the separate maintainer action described above.
GitHub may additionally require a maintainer to approve the actual bot-created
PR workflow run. Review and approve it through GitHub; a dispatched run alone may
not satisfy that pending PR check. Required checks remain enforced.
The [Plugin Repository](https://github.com/jellysin/repo) has separate ownership,
so this repository needs no cross-repository write token. All shared tooling is
pinned to full SHAs in [release-helper](https://github.com/jellysin/release-helper).

The engineering baseline is in
[JellySin principles](https://github.com/jellysin/.github/blob/main/docs/principles.md).
Keep third-party notices and Last.fm data rights separate from this code's EUPL-1.2.
