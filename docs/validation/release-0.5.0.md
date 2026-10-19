# Release 0.5.0

Release [v0.5.0](https://github.com/jellysin/plugin-lastfm/releases/tag/v0.5.0) was
published on 2026-10-19 at 09:31:34 UTC by run
[39227665516](https://github.com/jellysin/plugin-lastfm/actions/runs/39227665516) of
`release.yml`, dispatched by hand on the tag ref `refs/tags/v0.5.0`. The tag names
`9767280b250061adde637e874d696c5f747cf0a5` (`fix(web): keep the settings page's hidden
buttons hidden until it loads (#187)`), an ancestor of `main`. The values below were
read back after publication; the commands that produced them are listed so the check
can be repeated.

## Why the tag is not on the release commit

The release PR merged as `16a0672` (`chore(main): release 0.5.0 (#180)`), but CI on
that commit failed: the proxied sign-in check against the newest Jellyfin found the
settings page offering Connect before it knew the connection, so a click's refusal was
overwritten when the page finished loading. The tag was held until #187 fixed this and
merged as `9767280`, and was then created on that commit.

The changelog for 0.5.0 therefore does not list #187: release-please wrote it in #180,
before the fix existed. The release contains the changes the changelog lists plus #187,
the test change #181 and the dependency update #177; `git log v0.4.0..v0.5.0` lists them.

## Before the run

- Every check run on `9767280` had succeeded (CI, scanners, REUSE lint, four CodeQL
  analyses).
- `prepare-release.yml` run
  [39227562147](https://github.com/jellysin/plugin-lastfm/actions/runs/39227562147)
  created the tag and one unpublished draft.

## The run

All four jobs, `verify / CI`, `verify / scanners`, `build / build` and `publish`,
succeeded. The reproduction step (`tools/reproduce.py --tooling-directory
build/repro-tooling --reference-directory dist`) reported: "Two independent
exact-commit builds produced identical ZIP, metadata, SBOM and checksums."

## Published release

`gh release view v0.5.0 -R jellysin/plugin-lastfm --json isDraft,isImmutable,assets`
returned `isDraft: false`, `isImmutable: true` and four assets:

| Asset | SHA-256 |
| :--- | :--- |
| `plugin-lastfm-0.5.0.zip` | `d96498fadd577011aef33785b9e2be094cfbf9c8c2ca045de7ab4a47cfb78cc2` |
| `release.json` | `09c0399eebfe4c5536a3f3ce08ba59ece10d011695693993c2a1e8a749ec16f7` |
| `sbom.spdx.json` | `9fe89482b91a075f14a51faf9465fb43e76bb293e835ff3ecf487382dad2f1bd` |
| `checksums.txt` | `1cd3656db307b1fc2c662b63c8da950a5c304acaa9bb570904392e3cd528c827` |

The ZIP is 738512 bytes; its Jellyfin MD5 in `release.json` is
`02e1efb287808b585ebca535d4aa6698`, and `release.json` names commit `9767280b250061adde637e874d696c5f747cf0a5`.
`sha256sum -c checksums.txt` passed on the downloaded ZIP, `release.json` and
`sbom.spdx.json`.

## Attestation

`gh attestation verify <asset> -R jellysin/plugin-lastfm --signer-workflow
jellysin/plugin-lastfm/.github/workflows/build-signed.yml` passed for each of the four
downloaded assets. The same check with `release.yml` as the signer workflow refused the
ZIP.

## Catalog install

Not yet run for this release.

## Release workflows afterwards

`release.yml` (Publish exact release tag) had been turned off by hand. It was turned on
for this run and turned off again afterwards; it has no push or pull request trigger.
