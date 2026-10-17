# Validation record

Validation on 2026-10-12 used .NET SDK 10.0.401, Node 24.21.0, the repository
lockfiles and two pinned Jellyfin images, the oldest and the newest supported server:

- Jellyfin 12.0.0: `jellyfin/jellyfin:12.0@sha256:baba630419915985442f315f08b0cf46d9f4c8a0cc4bd38e94a6d35751dd5ef5`
- Jellyfin 12.2.0: `jellyfin/jellyfin:12.2@sha256:357724bf0ae27a672c7cbaa899db2d9abeb13dbd8657ccce750258a4c059d037`

The plugin is compiled against Jellyfin 12.0.0 and the same build is loaded on both
servers. Every server run asks the started server for its version and fails unless it
is the release the image tag names.

A third pinned image, Jellyfin 12.1.0
(`jellyfin/jellyfin:12.1@sha256:78d3ea1207d1322471fcac39a614f004f2ccf7e878f95ab2977d752f07e4dd7e`),
is used by the native fixture only, because 12.1 is the first release with the
playlist visibility rule described further down. The smoke harness and
the library load check were not run on 12.1 for this record; CI runs both there since
issue #63.

The table records the hosted run for `22a7a2e` with Python 3.14.8; every number in it
is read from the log of that run. The checks were not repeated locally for that
commit. GitHub
CI uses the versions pinned in `global.json`, `.node-version` and `.python-version` and
repeats the automated checks on proposed changes and release tags.

| Check | Observed result |
| --- | --- |
| .NET tests | 366 passed, none skipped |
| Unique production line coverage | 92.99% overall; 95.58% across API, configuration and transport |
| Restore, formatting, Release build | Locked restore; no formatting changes; zero warnings/errors |
| C# policy | 47 source files; no size or cognitive-complexity findings; analyzer self-tests passed |
| Dependency audits | No reported vulnerable dependencies in the five .NET projects. The npm production tree is empty, so the gating audit has nothing to check; six planted audit cases behaved as required. The installation of the complete npm tree, 86 packages, reported no vulnerabilities |
| Browser client unit tests | 18 passed, including byte limits, credential handling, immediate local logout and cancelled account requests |
| Browser scenarios | 56 passed in headless Chromium: root/prefixed paths, both sign-in races, late history responses, reviewed operations, safe metadata, the Last.fm sign-in with its return and every refused address on the dashboard and on the settings page, layout and performance |
| Accessibility | No axe violations in the tested signed-out and populated signed-in views at 320 and 1280 px |
| Frontend budget | Eight JavaScript modules total 13,447 bytes gzip; limit 150,000 bytes |
| Python helper tests | 142 passed: coverage validity, exact-tag/rebuild/upload-artifact comparison failures, the ABI floor and its proof against the release helper, server choice and release check, release preparation, the canary build, dependency audit, secret scan, commit-message attribution, the pinned helper and analyzer references, the proxied sign-in check and the library matching comparison |
| Repository configuration | Actionlint 1.7.12 and the shared release-helper check passed; `renovate.json` passed the published Renovate JSON schema in a local check, which is not part of the hosted run |
| Secret scans | Every fetched ref passed Gitleaks 8.30.1 with no blocking finding; four findings are allowed by unexpired single-fingerprint exceptions; six planted cases behaved as required |
| Reproducibility | Two clean checkouts reproduced all four artifacts and matched the selected upload set |
| Server checks | Smoke passed on Jellyfin 12.0.0 and 12.2.0; the native fixture on 12.0.0, 12.1.0 and 12.2.0; the proxied sign-in check and the library load budget on 12.2.0 |
| Hosted run | [CI 38250012315](https://github.com/jellysin/plugin-lastfm/actions/runs/38250012315) and [CodeQL 38250012386](https://github.com/jellysin/plugin-lastfm/actions/runs/38250012386) passed for `22a7a2e` |

The real Docker smoke harness loaded the plugin as UID 1000 on both servers and
verified embedded resources, security headers, ordinary-user password and Quick
Connect access, anonymous/API-key/cross-origin rejection, caller isolation,
configuration round trips, music scanning, restricted-library access, disabled
playback, root and `/jellyfin` deployments, a forwarding proxy and restart
persistence. These runs used a build without a Last.fm application. A build
containing the project application passed the same harness on 2026-09-08 with the
12.0 image only. The harness uses generated silent WAV fixtures and unique
disposable storage.

A sign-in check in a browser, `tests/integration/proxied_sign_in.py`, was added on
2026-10-11 after a build installed behind a proxy that ends TLS answered HTTP 403
to every changing request from the published address: the plugin compared the
scheme of the page with the scheme the server saw. The smoke harness had not
found this, because its proxy does not end TLS: page and server both used
`http`. The check starts
Jellyfin 12.2.0 with a published HTTPS address and no known proxy behind a proxy
that ends TLS, and a headless Chromium selects **Connect Last.fm**. Against the
defective build all scenarios at the published address failed. Against the
corrected build the local run passed all eight scenarios and the three checks of
the server log: from the published address the browser reached the address of
Last.fm's approval page with a return address under the published address, on the
dashboard and on the settings page, a preference was saved and Quick Connect
asked for its status; at the server's own HTTP port both pages stayed and named
both addresses to an administrator. Last.fm's approval page is a stub in this
check, so it shows no approval and no return from Last.fm. The smoke harness now
also sends writes with the published address, the request's own address, another
server, the other scheme and a cross-site mark as their origin.

The separate native integration fixture reproduced and then guarded against a
stale playback snapshot overwriting an imported count/date. Fresh native resets,
the actual userdata HTTP endpoint and favourite remove/re-add revisions passed.
For playlist recovery, a test-only one-shot fault delegated to the real native
manager, cleared one generated private playlist and raised an I/O failure. The
production journal/status and partial playlist survived restart; production
recovery restored exact track order, preserved user isolation and remained
idempotent. This fixture seeds the intended operation through the production
store, then explicitly triggers normal recovery for its unconnected account. It
does not claim that the Last.fm account scheduler ran for an unconnected user.
Repeated fixture preparation cannot overwrite an existing operation. The fixture
is a separate assembly, excluded from the production package. All of this passed
on Jellyfin 12.0.0 and 12.2.0, and in the later local run on 12.1.0.

The unit suite covers per-item daily-limit retention, failed outbox persistence,
shutdown drain, dispatch-time now-playing expiry, disconnect/reconnect races,
application identity, lost protection keys, disabled users, ambiguous matching,
incomplete favourite collections, interrupted additions, complete history-date
retrieval, resumable imports, cache expiration and storage reserves.

## Server changes after Jellyfin 12.0

Two server changes in Jellyfin 12.1 and 12.2 touch what the plugin relies on.

**Library matching.** Jellyfin 12.2 changed how artists are named and how names are
looked up. The native fixture scans a library with accented names, a name containing
a slash and one title by two artists, then asks the production matching for ten
tracks and nine artists or albums. Jellyfin 12.0.0, 12.1.0 and 12.2.0 gave the same
answers, which `tests/integration/native_library.py` records. The fixtures carry no
MusicBrainz identifiers and metadata providers are off, so this run does not show an
artist renamed after a MusicBrainz credit. A unit test covers that case in the
plugin: an album found by its identifier is matched even when the library spells the
album artist differently, while the name alone or a conflicting identifier does not
match it.

**Playlist visibility under a parental limit.** From Jellyfin 12.1 on, a playlist is
visible to a user only when the user's parental limit allows at least one of its
tracks (`HasParentalAllowedChild` in `Playlist.cs` is absent at tag `v12.0` and
present at `v12.1` and `v12.2`). Observed with the owner of a generated private
playlist: a limit that allows the tracks changes nothing on any of the three servers.
With unrated music blocked, the tracks are unreadable and unmatched on all of them;
Jellyfin 12.0.0 still lists the playlist, empty, while 12.1.0 and 12.2.0 neither list
nor return it. The plugin keeps the playlist recipe in every case, a second user never sees playlist or
recipe, and lifting the limit returns the playlist unchanged.

A refresh during the block runs against the Last.fm stand-in with a connected owner
(`blocked_refresh_checks` in `tests/integration/native_userdata.py`). The refresh
reads the owner's top tracks, matches none of them because every track is blocked,
and answers HTTP 409 before it looks the playlist up or writes to it; no pending write
remains, and after the limit is lifted the playlist and its recipe are unchanged. The
same refresh without the limit rewrites the playlist from the top tracks. Observed on
Jellyfin 12.2.0 on 2026-10-17; CI runs the check on the 12.0, 12.1 and 12.2
servers. When some tracks stay allowed, the plugin looks the playlist up as
its owner and stops with "unavailable" if the server does not return it: with the
empty-match refusal removed, the 12.2.0 server answered that lookup with HTTP 403.

A third change, the similar-items cache that Jellyfin 12.1 writes through a temporary
file, does not concern the plugin: it offers the server no similarity provider.

## Observed on 2026-09-08 with the 12.0 image only

The observations in this section need the project application and a live Last.fm
account. They were made with the 12.0 image and have not been repeated on 12.2.

The project application completed the Last.fm browser token/session flow. Live
history, period charts/statistics and discovery returned data. One explicitly
authorized 65-second generated fixture was observed for 35.16 real seconds and
submitted through Jellyfin playback callbacks and the durable outbox. Last.fm
returned exactly one matching completed recent entry; pending/blocked submissions,
dropped snapshots and failed writes were all zero. Further scrobbling was disabled
on the isolated account afterward. This observation does not establish exactly-once
delivery under every network failure. Private history and credentials are not
published with validation artifacts.

The candidate's final live-browser checks passed with the project application:
native administrator status/form/configuration save, ordinary-user history/charts/
discovery and axe at desktop/320 px. A generic native-browser exception occurred
in the first run; neither the isolated native rerun nor the complete rerun
reproduced it. Final reports recorded no page errors or attempted Last.fm writes.
The existing connection stayed valid with scrobbling/favourites paused and no
pending deliveries.

The native-browser exception has not come back. Since 2026-10-13 every CI run opens
the plugin's pages on the newest Jellyfin 12 with `tests/integration/page_check.py`,
with a Last.fm stand-in instead of the live service, and a page error fails that
step. By 2026-10-17, 34 runs on `main` had passed it, and no failed run in that
time printed a page error. On 2026-10-17 the same check passed without page errors
on 12.0.0, 12.1.0 and 12.2.0. This closes the limit; the paragraph above stays as
the record of the 2026-09-08 run.

## Observed on 2026-10-12 with Jellyfin 12.2.0

A Jellyfin 12.2.0 server with the project application and a live Last.fm account ran
the canary build 0.0.7 of commit `d8e7364`, not a release build. The account owner
used the pages; the observations come from the server log, read without changes.

- The Last.fm browser token/session flow completed: the return was accepted and the
  account connected.
- A played track produced a playing-now update that Last.fm accepted, and one play
  submitted afterwards: one sent, Last.fm accepted one and ignored none.
- A play that was running while the account signed in again was not submitted. By the
  code, signing in renews the capture generation and the tracker drops a play that
  started under the old one; this was read from the code, not traced.
- Saving the favourites preference and **Sync now** ended without HTTP 408. With build
  0.0.5 of commit `77870ea`, which ran the reconciliation inside the web request,
  both had ended with 408 earlier that day.
- Background favourite runs paused six times, each with the warning naming the
  `FirstPageChanged` check: 337 tracks reported and read over two pages, all
  distinct, and page 1 differed when read again. A pause writes nothing. Why page 1
  differs is not yet known.

History and charts views, discovery results and the native administrator form were
not observed in this run; the page check below covers them without a live account.

## Page check on 2026-10-12 with Jellyfin 12.2.0

[`tests/integration/page_check.py`](../tests/integration/page_check.py) from pull
request head `97096e5`, identical in the check's files to `5ce094d` on `main`, ran on 2026-10-12 at 21:14 (+02:00) against the pinned 12.2.0 image with the plugin
built from `d8e7364` (DLL SHA-256
`6dccf9902560361737105e9123c951f4c7eb0f59e13c0d0ce5aca506dd51d59a`). The server ran
on a Docker network made for the run and reached Last.fm only through the stand-in
`lastfm_stand_in.py`, which answers the documented methods with fixture data. No
Last.fm account, project application or credential was used; the application the
administrator saved is made up. Headless Chromium passed all five checks:

- Administrator settings page: the plugin settings and an application override saved
  with HTTP 204 each, and the account section followed the new application.
- An ordinary user connected through the browser grant and the page showed
  "Connected as stand-in-listener".
- Charts listed tracks, artists and albums.
- Recent listens listed the fixture plays.
- One discovery result listed a match in the library and one result beyond it.

The server asked the stand-in for `auth.getSession` and eight read methods, each once.
The stand-in opened one Last.fm tunnel and refused the server's own requests to
`repo.jellyfin.org` and `musicbrainz.org`. The run checks that the server log does not
contain the session key. The stand-in does not prove behavior against the live
Last.fm service; the live views remain those observed on 12.0.0.

## Measurements

Callback and library load measurements and their workload limits are in
[benchmarks/README.md](../benchmarks/README.md). They are measurements of this
implementation, not claims of improvement over an inherited one.

## Release availability

Release [0.1.0](https://github.com/jellysin/plugin-lastfm/releases/tag/v0.1.0) was published on 2026-10-17; its attestation, immutability and
reproduction are recorded in [validation/release-0.1.0.md](validation/release-0.1.0.md).
Release [0.2.0](https://github.com/jellysin/plugin-lastfm/releases/tag/v0.2.0) followed on the same day; its record is
[validation/release-0.2.0.md](validation/release-0.2.0.md).
The checks above are evidence for the commit they name.
The separate Last.fm terms questions remain documented; no provider confirmation
is claimed. Native client
behavior beyond the tested host surfaces must not be inferred from unit coverage.
