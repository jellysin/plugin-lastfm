# Continue from another machine

Updated 2026-09-08 after the first release rollout. Clone or pull the independent
repositories under https://github.com/jellysin: `jellyfin-plugin-lastfm`,
`plugin-tooling`, `catalog` and `.github`. Use each repository's `main` branch and
read its AGENTS.md and CONTRIBUTING.md before changes.

## Completed

- JellySin Last.fm 1.0.0 is published, immutable and installed successfully from
  the public catalog on a fresh Jellyfin 12 host. The installed version is 1.0.0.0;
  its minimum ABI is 12.0.0.0. See [validation](validation.md) for test scope and
  portable release/install reports.
- Shared build-only tooling is 1.0.3. Its 88 tests cover independent plugin
  identities, deterministic packaging, provenance, real GitHub draft behavior,
  interrupted uploads and safe publication retry. Installed plugins need no Python.
- The catalog is
  `https://raw.githubusercontent.com/jellysin/catalog/main/manifest.json`.
  Approved releases are polled every 30 minutes; a verified catalog PR still needs
  its actual CI run reviewed/approved and a normal squash merge.
- The optional Last.fm permission-request draft was removed at the maintainer's
  request. No message was sent. Factual API terms and data-use limits remain in
  [lastfm-api.md](lastfm-api.md).

## Development and credentials

Run the checks in CONTRIBUTING.md using the pinned SDK, Node and Python versions.
Ordinary builds require no Last.fm secret; production release workflows use the
existing repository secrets `LASTFM_API_KEY` and `LASTFM_SHARED_SECRET`.
Those secrets, user connections and private live-test state are not in Git.
The registered application remains accessible through the maintainer's Last.fm
account; do not create another application merely to resume development.

One explicitly authorized live scrobble was already verified. The retained test
connection has scrobbling and favourite sync paused. Do not rerun a live write test
without fresh authorization. Automated tests use synthetic fixtures and isolated
servers. No home Jellyfin installation is implied by the disposable install test.

## Releases and historical state

The original `v1.0.0` tag remains at
`ae3b4b19ecc0f41761760c73a8c9910664815920`. Its first publisher exposed draft API
assumptions, corrected in tooling 1.0.2/1.0.3. The reviewed recovery workflow
published the original signed bytes and passed an immutable retry. Keep its
historical source/package pins frozen. Later releases use the normal tag workflow
and current shared tooling. Never move the published tag or replace assets.

The old `lusoris/jellyfin-plugin-lastfm` and `lusoris/jellyfin-utilities` repositories
have migration notices and are archived as historical references. Their published
releases, catalogs, licenses and credits are preserved. The unfinished modernization PR stays unmerged; do not merge it
into the new implementation. Fresh work belongs in the JellySin repositories.
The preservation report records published asset identities, tags and catalog
blobs. GitHub hides the pre-existing unpublished draft after archival; no release
was deleted, and its prior metadata remains in the local preservation snapshot.

All requested first-release code and validation are recorded in Git. Future work
should start with a concrete issue or reproduced defect; the acceptance evidence
does not promise every Jellyfin client or every third-party API failure is covered.
