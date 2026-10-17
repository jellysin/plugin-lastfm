# Release 0.1.0

Release [v0.1.0](https://github.com/jellysin/plugin-lastfm/releases/tag/v0.1.0) was
published on 2026-10-17 at 19:07:04 UTC by run
[39060252451](https://github.com/jellysin/plugin-lastfm/actions/runs/39060252451) of
`release.yml`, dispatched by hand on the tag ref `refs/tags/v0.1.0`. The tag names
`2da94661557abba9d99e0a6eebc2b230d9aadcfe` (`chore(main): release 0.1.0 (#108)`), an
ancestor of `main`. The values below were read back after publication; the commands
that produced them are listed so the check can be repeated.

## Before the run

- Every check run on `2da9466` had succeeded (CI, tag and draft, four CodeQL analyses).
- One unpublished draft existed for the tag, with no assets.
- Immutable releases were enabled for the repository, and the `LASTFM_API_KEY` and
  `LASTFM_SHARED_SECRET` secrets existed.

## The run

Both jobs, `verify / CI` and `publish`, succeeded. In `publish`, every step succeeded,
in this order: the tag ancestry check, the credential validation, the production
build, the release helper's package step, the second checkout of the packaging
tooling, the reproduction, the attestation and the publication. The reproduction step
(`tools/reproduce.py --tooling-directory build/repro-tooling --tag v0.1.0
--reference-directory dist`) reported: "Two independent exact-commit builds produced
identical ZIP, metadata, SBOM and checksums."

## Published release

`gh release view v0.1.0 -R jellysin/plugin-lastfm --json isDraft,isImmutable,assets`
returned `isDraft: false`, `isImmutable: true` and four assets:

| Asset | SHA-256 |
| :--- | :--- |
| `plugin-lastfm-0.1.0.zip` | `a3cb5511925f3145c3565bbde4034cc30dab3977ff6c29cc59af94cf56511ff0` |
| `release.json` | `9848ace9d30e5b582f43c88d070cc1441095a3e1183dd53f6087cc97e43f06d7` |
| `sbom.spdx.json` | `25a081b1c0f180417aadd1389d9370df6278ef4a5d6a09d6c13bff0a559f2d5a` |
| `checksums.txt` | `f7cc77c37992d571409fd80a9d8e475c90abe0111c21ddb7a843525d98eb5183` |

The ZIP is 533712 bytes; its Jellyfin MD5 in `release.json` is
`c46ab64410c372ecf5f13b2df3eadcf1`. `sha256sum -c checksums.txt` passed on the
downloaded ZIP, `release.json` and `sbom.spdx.json`.

## Attestation

`gh attestation verify <asset> -R jellysin/plugin-lastfm` passed for each of the four
downloaded assets. Each attestation is SLSA provenance v1, signed for
`.github/workflows/release.yml@refs/tags/v0.1.0`, source digest
`2da94661557abba9d99e0a6eebc2b230d9aadcfe` and run 39060252451, attempt 1. The ZIP's
entry in the public transparency log has index 3299431495.

## Catalog install

After the Plugin Repository listed 0.1.0.0 (jellysin/repo#49, merged as `fff77b2`), a
fresh Jellyfin 12.2.0 server (image digest
`sha256:357724bf0ae27a672c7cbaa899db2d9abeb13dbd8657ccce750258a4c059d037`) installed
the release from `https://raw.githubusercontent.com/jellysin/repo/main/manifest.json`
on 2026-10-17. The run used the startup wizard and client of `tools/smoke.py`:

1. Before the install the server listed only its bundled plugins and held no JellySin
   files.
2. `POST /Repositories` added the catalog, `GET /Packages` offered the plugin's GUID and
   `POST /Packages/Installed` installed version 0.1.0.0, which the server then listed
   with status `Restart`.
3. After each of two restarts `GET /Plugins` listed `JellySin Last.fm` 0.1.0.0 as
   `Active` and the plugin's configuration page answered 200.

The installed `JellySin.Plugin.Lastfm.dll` has SHA-256
`7f5cde943dddb6797227fef67c2acd372e115dd4abef57190166d7d93077b0b2`, the same as the
assembly inside `plugin-lastfm-0.1.0.zip`. The same run against the catalog before
#49, which listed no plugin, stopped with "The server does not offer the package."

## Release workflows afterwards

`release.yml` (Publish exact release tag) had been turned off by hand. It was turned on
for this run and turned off again afterwards; it has no push or pull request trigger.
`release-automation.yml` (Prepare release PR) stayed turned off throughout: the release
PR for 0.1.0 had already merged as #108.
