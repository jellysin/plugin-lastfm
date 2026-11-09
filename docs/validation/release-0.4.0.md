# Release 0.4.0

This record was written on 2026-11-09, after publication, from read-back. Release
[v0.4.0](https://github.com/jellysin/plugin-lastfm/releases/tag/v0.4.0) was
published on 2026-10-18 at 15:05:49 UTC by run
[39137087325](https://github.com/jellysin/plugin-lastfm/actions/runs/39137087325) of
`release.yml`, dispatched by hand on the tag ref `refs/tags/v0.4.0`. The tag names
`241e1e1b54440f1874cff0b464018bb53c2b7529` (`chore(main): release 0.4.0 (#175)`), an
ancestor of `main`. The values below were read back after publication; the commands
that produced them are listed so the check can be repeated.

## Before the run

- On `241e1e1`: CI run
  [39135161866](https://github.com/jellysin/plugin-lastfm/actions/runs/39135161866),
  CodeQL run
  [39135161875](https://github.com/jellysin/plugin-lastfm/actions/runs/39135161875) and
  REUSE run
  [39135161908](https://github.com/jellysin/plugin-lastfm/actions/runs/39135161908)
  all succeeded (push, 2026-10-18 14:15 UTC).
- `prepare-release.yml` run
  [39136991455](https://github.com/jellysin/plugin-lastfm/actions/runs/39136991455)
  (workflow_dispatch, 2026-10-18 14:39 UTC) succeeded.

## The run

All four jobs, `verify / CI`, `verify / scanners`, `build / build` and `publish`,
succeeded in `release.yml` (Publish exact release tag) run
[39137087325](https://github.com/jellysin/plugin-lastfm/actions/runs/39137087325)
(workflow_dispatch on tag `v0.4.0`, 2026-10-18 14:40 UTC).

## Published release

`gh release view v0.4.0 -R jellysin/plugin-lastfm --json isDraft,isImmutable,publishedAt,assets`
returned `isDraft: false`, `isImmutable: true` and `publishedAt: 2026-10-18T15:05:49Z`,
with four assets (SHA-256 from release API, matched by download):

| Asset | SHA-256 |
| :--- | :--- |
| `plugin-lastfm-0.4.0.zip` | `9639295da227e8b8ea9aea55a0fb4aaf0652f97c1ff2bb687aff34744c50657a` |
| `release.json` | `dbe7a38eb097725ed235c45de5747a0d517f0e5607073cd3a6235228ceb17f07` |
| `sbom.spdx.json` | `7293ce8ca0251fb8f8e75e5b31f1cdf99c7d2eea3538f73593c40022593bcd25` |
| `checksums.txt` | `c13513b23b04719c1150def81169ac17d407405cdb6033aac99cb4ce1ac0ca3a` |

The ZIP is 639184 bytes; its Jellyfin MD5 in `release.json` is
`028e61c7f1c63d673d0ed9a9de0e8159`, and `release.json` names version `0.4.0.0` and
commit `241e1e1b54440f1874cff0b464018bb53c2b7529`. `sha256sum -c checksums.txt` passed
for the ZIP, `release.json` and `sbom.spdx.json`.

## Attestation

`gh attestation verify <asset> -R jellysin/plugin-lastfm --signer-workflow
jellysin/plugin-lastfm/.github/workflows/build-signed.yml` passed for all four assets;
with `release.yml` as signer workflow it refused the ZIP.

## Catalog install

Not run for this release. The Plugin Repository listed 0.4.0.0 from commit f8f6c4d of
jellysin/repo onwards.
