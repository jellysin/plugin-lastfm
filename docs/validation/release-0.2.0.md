# Release 0.2.0

Release [v0.2.0](https://github.com/jellysin/plugin-lastfm/releases/tag/v0.2.0) was
published on 2026-10-17 at 22:12:45 UTC by run
[39072450506](https://github.com/jellysin/plugin-lastfm/actions/runs/39072450506) of
`release.yml`, dispatched by hand on the tag ref `refs/tags/v0.2.0`. The tag names
`00eb6863a67f1de6cf5935415bbcda89f96a06ff` (`chore(main): release 0.2.0 (#115)`), an
ancestor of `main`. The values below were read back after publication; the commands
that produced them are listed so the check can be repeated.

## Before the run

- Every check run on `00eb686` had succeeded (CI, tag and draft, four CodeQL analyses).
- Two unpublished drafts existed for the tag, both without assets: one written by the
  merge train that also writes the release PR, and one written by the Prepare release
  workflow. The release helper refuses a tag that more than one release matches, so the
  Prepare release copy was deleted and the merge train's draft was published. Why both
  writers create a draft is tracked separately.
- Immutable releases were enabled for the repository, and the `LASTFM_API_KEY` and
  `LASTFM_SHARED_SECRET` secrets existed.

## The run

Both jobs, `verify / CI` and `publish`, succeeded. In `publish`, every step succeeded,
in this order: the tag ancestry check, the credential validation, the production
build, the release helper's package step (release-helper v1.2.2), the second checkout
of the packaging tooling, the reproduction, the attestation and the publication. The
reproduction step (`tools/reproduce.py --tooling-directory build/repro-tooling --tag
v0.2.0 --reference-directory dist`) reported: "Two independent exact-commit builds
produced identical ZIP, metadata, SBOM and checksums."

## Published release

`gh release view v0.2.0 -R jellysin/plugin-lastfm --json isDraft,isImmutable,assets`
returned `isDraft: false`, `isImmutable: true` and four assets:

| Asset | SHA-256 |
| :--- | :--- |
| `plugin-lastfm-0.2.0.zip` | `ed915fe6df5e1cf58cb12f4d2b5012971a4b4af92c220bb1341909347c454496` |
| `release.json` | `44cb30879caf17b97c1e16bba46cc713189934f3870c92b457336ee199d7aa88` |
| `sbom.spdx.json` | `6f3d523429758df3f4167e4bf925e9887889caf09f4f847007596925fc01d7b6` |
| `checksums.txt` | `1e4e0294332a4ab908c5f6a46754bb5fb381ad2d0e50ef33d895f9fdbae22e01` |

The ZIP is 619216 bytes; its Jellyfin MD5 in `release.json` is
`1fa9e94f0852f34f3c053a4629b9bc67`. `sha256sum -c checksums.txt` passed on the
downloaded ZIP, `release.json` and `sbom.spdx.json`.

## Attestation

`gh attestation verify <asset> -R jellysin/plugin-lastfm` passed for each of the four
downloaded assets. Each attestation is SLSA provenance v1, signed for
`.github/workflows/release.yml@refs/tags/v0.2.0`, source digest
`00eb6863a67f1de6cf5935415bbcda89f96a06ff` and run 39072450506, attempt 1. The ZIP's
entry in the public transparency log has index 3301584754.

## Catalog install

The first run of the Plugin Repository's update workflow after publication refused the
release with "SBOM differs from release identity, files, checksums or dependency
graph". The catalog still verified with release-helper v1.2.1, while 0.2.0 was packaged
with v1.2.2, which names itself `jellysin-release-helper` in the SBOM creator field.
Checked locally against the downloaded assets, the v1.2.1 SBOM validation refused the
release and the v1.2.2 validation accepted it. 0.1.0 was not affected: the catalog
skips versions it already lists. jellysin/repo#47 moved the catalog to v1.2.2
(merged as `97d1fe6`), after which the workflow opened jellysin/repo#51 with the
0.2.0.0 entry; its checksum is the MD5 above.

After #51 merged as `e339630`, a fresh Jellyfin 12.2.0 server (image digest
`sha256:357724bf0ae27a672c7cbaa899db2d9abeb13dbd8657ccce750258a4c059d037`) installed
the release from `https://raw.githubusercontent.com/jellysin/repo/main/manifest.json`
on 2026-10-17. The run used the startup wizard and client of `tools/smoke.py`:

1. Before the install the server listed only its bundled plugins and held no JellySin
   files.
2. `POST /Repositories` added the catalog, `GET /Packages` offered the plugin's GUID with
   versions 0.2.0.0 and 0.1.0.0, and `POST /Packages/Installed` installed version
   0.2.0.0, which the server then listed with status `Restart`.
3. After each of two restarts `GET /Plugins` listed `JellySin Last.fm` 0.2.0.0 as
   `Active` and the plugin's configuration page answered 200.

The installed `JellySin.Plugin.Lastfm.dll` has SHA-256
`c94061c8316f5736e9104dad4b226c923647ba0758242c22c4f25b9dc243c3b6`, the same as the
assembly inside `plugin-lastfm-0.2.0.zip`.

## Release workflows afterwards

`release.yml` (Publish exact release tag) had been turned off by hand. It was turned on
for this run and turned off again afterwards; it has no push or pull request trigger.
`release-automation.yml` (Prepare release PR) stayed turned off throughout: the release
PR for 0.2.0 had already merged as #115.
