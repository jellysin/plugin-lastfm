# Contributing

Use a feature branch from main and a Conventional Commit PR title. Squash merging
keeps reviewed changes attributable in release notes. Required checks use
`strict: false`; there is no branch-up-to-date requirement. This repository targets
Jellyfin 12 only, using the .NET SDK pinned in `global.json` and committed exact
NuGet locks.

Commit messages and pull request texts credit people and bots only. A co-author
trailer or a generated-with footer that names a model or coding tool is refused by
`python tools/attribution.py`, which CI runs over the whole history and over the
title and text of each pull request.

## Build and validate

Use the Node and Python versions pinned in `.node-version` and `.python-version` for
the web build and validation helpers. Web
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
runs actionlint and Gitleaks, packages the DLL, and exercises real Jellyfin 12
containers, the oldest supported and the newest, through `tools/smoke.py`
(`--server floor` or `--server newest`, the newest started with a published
server address that the Last.fm sign-in must name as its return), and on Jellyfin
12.1 (`--server 12.1`) the smoke harness, the library load budget and the native
userdata check. On the newest server
`tests/integration/proxied_sign_in.py` starts the Last.fm sign-in in a browser
behind a proxy that ends TLS and compares the server log with the clicks; its
README section names what it needs to run locally. `tests/integration/page_check.py`
then opens the plugin's pages there with a Last.fm stand-in on the run's own Docker
network; a page that fails its check fails the step. CodeQL scans source and
Actions separately.
The secret scan covers every fetched ref. An exception is one full fingerprint in
`.gitleaksignore` with a reason and an expiry date; from that date the finding fails
again. `tools/secret_scan.py --self-test` plants keys and an expired exception first.
Pull requests fail on advisories in production dependencies only; that tree is empty
today. `.github/workflows/audit.yml` audits the whole lock every day, fails on a finding
and keeps one open issue for it. `tools/dependency_audit.py --self-test` plants a
vulnerable lock first. Renovate raises security fixes without the three-day wait.
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
The first release PR targets `0.1.0`; later releases stay on 0.x minors until the
product feature list is complete, and only then does a release PR target `1.0.0`.

Stable `vX.Y.Z` tags map to `X.Y.Z.0` for the assembly and Plugin Repository;
the minimum Jellyfin ABI is separately recorded in `plugin.json`.
Do not ship host-provided assemblies.

`targetAbi` in `plugin.json` is the floor: the oldest Jellyfin server the plugin
supports. Every `Jellyfin.*` package reference and `runtime-dependencies.json` stay
at exactly that version. Jellyfin stamps each release into the versions of its
assemblies, so a plugin compiled against a newer release is disabled as incompatible
by an older server, while a plugin compiled against the floor loads on the releases
above it. Two checks hold the references there, and Renovate does not propose these
packages:

- The release helper's package step, in CI and in the release workflow, refuses the
  plugin project's locked references and `runtime-dependencies.json` when they differ
  from the floor. `tools/floor_proof.py` plants a reference above and one below the
  floor in every CI run and fails unless the pinned helper packages the unchanged
  references and refuses both planted ones for the floor's sake.
- `python tools/abi_floor.py` and the Python tests fail on a differing reference in
  the test, benchmark and native probe projects.

Raising the floor changes `targetAbi`, the references and the lockfiles in one pull
request and ends support for the servers below it.

`tools/smoke.py` is the only file that names the server images: `floor` is the
release in `targetAbi`, `newest` the latest Jellyfin 12. CI runs the load, isolation,
restart and native userdata checks on both, and each run fails unless the server
that answered reports the release its image tag names. Renovate moves the digest of
both and the release of `newest` only; `tools/abi_floor.py` also fails when the `floor`
image or its Renovate rule would leave the floor. Raising the floor changes both
with it.

An entry named after a release, such as `12.1`, keeps that release because a check
depends on what changed in it: Jellyfin 12.1 is the first release that hides a
playlist whose tracks a parental limit blocks, and the smoke harness, the library
load budget and the native userdata check run there as well. Renovate moves only its digest, and the Python tests fail when the
entry, its Renovate rule or the pattern Renovate reads the entries with would let it
run another release.

A maintainer separately decides when to publish. After reviewing and merging the
release PR, dispatch `prepare-release.yml` on `main` with the version tag and the full
SHA of the merged release commit. The release helper's prepare step checks that the
commit is on main and records the version in the tag, refuses a tag that names another
commit or a release that is already published, then creates the tag and one unpublished
draft whose text is the version's section of `CHANGELOG.md`. It creates only what is
missing, so a failed run is repeated with the same values. It publishes nothing, and a
tag created this way starts no workflow. The Python tests fail when the workflow gains
a trigger, a write permission beyond the one job's `contents`, a script or another step.

Then manually dispatch `release.yml` **on that tag ref**. This binds GitHub provenance
to the source tag. The publication workflow has no push or PR trigger and is never
dispatched by release-please. It reruns CI and builds production bytes. Packaging verifies the NuGet
production graph against the committed lock and restored assets, then creates a
deterministic ZIP, SPDX file/dependency inventory and checksums. The release metadata
carries the version's section of `CHANGELOG.md` as the text the Plugin Repository
shows; a tagged build without that section fails. Independent builds
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

`canary.yml` builds main with the same application for a trial on one server before a
release exists. A maintainer dispatches it by hand; it refuses every ref but main,
creates no tag, draft or catalog entry and keeps its artefact for one day. The build is
versioned `0.0.<run>`, below every release, and its archive is named
`plugin-lastfm-canary-*`, so it cannot pass as a release. The artefact carries the
application as a release does. `tools/canary.py` refuses a build without the application
or under another version, and the Python tests fail when the workflow gains a trigger,
a write permission, a longer retention or a script that names the application.
A plugin copied in by hand is listed in no Plugin Repository, so Jellyfin's page for it
shows "An error occurred while getting the plugin details from the repository." This is
expected for a canary and says nothing about the plugin itself.

Only the built-in repository token is used. Bot PR CI is explicitly dispatched;
publication requires the separate maintainer action described above.
GitHub may additionally require a maintainer to approve the actual bot-created
PR workflow run. Review and approve it through GitHub; a dispatched run alone may
not satisfy that pending PR check. Required checks remain enforced.
The [Plugin Repository](https://github.com/jellysin/repo) has separate ownership,
so this repository needs no cross-repository write token. All shared tooling is
pinned to full SHAs in [release-helper](https://github.com/jellysin/release-helper).
The Python tests fail unless every workflow reference to it names the same commit and
the release that commit is.

The engineering baseline is in
[JellySin principles](https://github.com/jellysin/.github/blob/main/docs/principles.md).
Keep third-party notices and Last.fm data rights separate from this code's EUPL-1.2.
