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

The Plugin Repository's update workflow opened jellysin/repo#67 on 2026-10-19; its last
head, `c4a8933` (2026-11-07), adds only the 0.5.0.0 entry, with `targetAbi` 12.0.0.0 and
the MD5 above. GitHub held the pull request's CI and CodeQL runs for approval
(`action_required`), and the CI runs the workflow dispatched on its branch do not count
toward the required checks. The CodeQL run
[42411366029](https://github.com/jellysin/repo/actions/runs/42411366029) and the CI run
[42411365973](https://github.com/jellysin/repo/actions/runs/42411365973) were approved
and passed, and #67 merged as `faeb534` on 2026-11-10.

A fresh Jellyfin 12.2.0 server (image digest
`sha256:357724bf0ae27a672c7cbaa899db2d9abeb13dbd8657ccce750258a4c059d037`) then installed
the release from `https://raw.githubusercontent.com/jellysin/repo/main/manifest.json` on
2026-11-10, which listed 0.5.0.0, 0.4.0.0, 0.2.0.0 and 0.1.0.0.
`tests/integration/catalog_install.py` makes this check with the startup wizard and
client of `tools/smoke.py`. This command, run from the repository root, passed:

```sh
python3 tests/integration/catalog_install.py --version 0.5.0.0 --server 12.2 \
  --report build/catalog-install-0.5.0.json
```

Its report records each step:

1. Before the install `GET /Plugins` listed no JellySin plugin.
2. `POST /Repositories` added the catalog, `GET /Packages` offered the plugin's GUID, and
   this request installed version 0.5.0.0, which the server then listed with status
   `Restart` (query values URL-encoded in the request):

   ```text
   POST /Packages/Installed/JellySin%20Last.fm?assemblyGuid=2034650d-a290-4a16-b195-89fb44cfb932&version=0.5.0.0&repositoryUrl=https://raw.githubusercontent.com/jellysin/repo/main/manifest.json
   ```

3. After each of two restarts `GET /Plugins` listed `JellySin Last.fm` 0.5.0.0, and only
   that version, as `Active`.

Two different digests tie the install to the release:

- The installed `JellySin.Plugin.Lastfm.dll` has SHA-256
  `32a557fd143cae7b866607a7aa715da0f4c22e6b436bad079a8f4c3296d57e8b`. That is the digest
  of the DLL file inside the downloaded ZIP, not of the ZIP.
- The downloaded `plugin-lastfm-0.5.0.zip` has SHA-256
  `d96498fadd577011aef33785b9e2be094cfbf9c8c2ca045de7ab4a47cfb78cc2`, the ZIP digest in
  the asset table above.

The check also refuses when it should. On 2026-11-10 before #67 merged, an earlier,
uncommitted version of the script failed because the catalog on `main` did not offer
0.5.0.0: the same install request answered 404. After the merge, `--plant
none-installed` makes the script judge the listing from before the install in place of
the installed one. `tools/expect_refusal.py` passes only when that run exits 1 and its
report names the empty listing, and this command passed:

```sh
python3 tools/expect_refusal.py --report build/catalog-install-0.5.0-planted.json \
  --expect "Before the install the server lists []" \
  -- python3 tests/integration/catalog_install.py --version 0.5.0.0 --server 12.2 \
  --plant none-installed --report build/catalog-install-0.5.0-planted.json
```

## Release workflows afterwards

`release.yml` (Publish exact release tag) had been turned off by hand. It was turned on
for this run and turned off again afterwards; it has no push or pull request trigger.
