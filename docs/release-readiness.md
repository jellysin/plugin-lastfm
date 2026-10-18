# Release readiness

The plugin is in development; the first release is [0.1.0](https://github.com/jellysin/plugin-lastfm/releases/tag/v0.1.0), recorded in
[validation/release-0.1.0.md](validation/release-0.1.0.md); the latest is
[0.2.0](https://github.com/jellysin/plugin-lastfm/releases/tag/v0.2.0), recorded in
[validation/release-0.2.0.md](validation/release-0.2.0.md). The candidate validation
below records observed checks.

The plugin is compiled against Jellyfin 12.0.0 and supports Jellyfin 12 from there
up. The automated server checks run on Jellyfin 12.0.0 and 12.2.0 on every change, and
the native fixture, the smoke harness and the library load budget also on 12.1.0.
Observations named for 12.0.0 were made on 2026-09-08 with the project application
and a live Last.fm account on the 12.0 image. The Last.fm browser grant and a live
scrobble were repeated on 12.2.0 on 2026-10-12 with a canary build
([validation record](validation.md#observed-on-2026-10-12-with-jellyfin-1220)). The
same day the history and charts views, a discovery result and the native administrator
form passed on 12.2.0 in a browser against a Last.fm stand-in, without a live account
([page check](validation.md#page-check-on-2026-10-12-with-jellyfin-1220)).

## Evidence and remaining work

| Requirement | Evidence / outstanding verification |
| --- | --- |
| Fresh Jellyfin 12 implementation | Independent history, GUID and namespace; .NET 10; no 10.11 compatibility branch; one build loaded on Jellyfin 12.0.0 and 12.2.0 |
| Account connection | Registered project application; real ordinary-user password/Quick Connect passed on 12.0.0 and 12.2.0; Last.fm browser grant passed on 12.0.0 and on 12.2.0 with a canary build |
| Credential protection | Protected sessions, authorization headers, redaction, per-user auth cancellation, disabled users and application-change regressions passed |
| Playback and durable delivery | One explicitly authorized live scrobble passed on 12.0.0, and one live scrobble on 12.2.0 with a canary build; daily-limit retention, account transitions, enqueue retry and shutdown drain regressions passed |
| Favourite reconciliation | Additive merge, ambiguous identities, reviewed removals, intervening edits and interrupted-addition recovery tested; native revision protection passed |
| History and charts | Live views passed on 12.0.0; on 12.2.0 the views passed against a Last.fm stand-in; bounded complete-date retrieval/resumable application tested; real native stale-write protection and deliberate resets passed on 12.0.0 and 12.2.0 |
| Personalized discovery | Real local/external results and unknown-seed behavior verified on 12.0.0; on 12.2.0 one local and one external result passed against a Last.fm stand-in; private cache-first/expiration tests passed; library matching gave the same recorded answers on 12.0.0, 12.1.0 and 12.2.0 |
| Private generated playlists | Actual ownership, regeneration, injected native write failure, durable restart recovery and retry passed on 12.0.0 and 12.2.0; reviewed cancellation tested; visibility under the owner's parental limit recorded for 12.0.0, 12.1.0 and 12.2.0 |
| Storage and lifecycle | Atomic updates, cancellation, byte/document limits, durable reserves and orphan cleanup regressions passed |
| Web page | 56 browser scenarios, 18 client tests, root/prefixed paths, immediate logout, safe rendering, review flows, axe, keyboard and performance budgets passed; final native admin form/live read-only checks passed on 12.0.0; on 12.2.0 the native admin form saved settings and an application in a browser |
| Large-library behavior | Real 10,000-track Jellyfin 12.2.0 server with eight concurrent search/playback HTTP clients passed locally and in hosted CI; a local run on 12.0.0 passed the same budget |
| Callback performance | Production callbacks passed one/four-producer latency/allocation budgets with 256 linked accounts and zero dropped/failed writes |
| Release tooling | The tooling revision tested on 2026-09-08 passed 67 tests with two distinct plugin fixtures, SBOM inventory, strict validation and interrupted-upload retry |
| Reproducibility | Two independent checkouts matched the actual selected ZIP, release metadata, SBOM and checksums locally and in hosted CI; production tag repeats the gate with embedded credentials |
| Provenance, SBOM and signatures | [`build-signed.yml`](../.github/workflows/build-signed.yml) builds and packages the plugin, attests build provenance and the release-helper SPDX SBOM, and signs each artifact with cosign keyless in one reusable job; [`release-rehearsal.yml`](../.github/workflows/release-rehearsal.yml) runs it on `main` and checks the result in a separate job with `gh attestation verify` and `cosign verify-blob` through [`verify-signed`](../.github/actions/verify-signed/action.yml), without creating a tag, release or draft. [`release.yml`](../.github/workflows/release.yml) builds the tag through the same reusable workflow and publishes only after the same `verify-signed` check passes. On 2026-10-18 the clean rehearsal on `main` passed both verification steps ([run 39093705666](https://github.com/jellysin/plugin-lastfm/actions/runs/39093705666)), and the rehearsal with `tamper` set, which appends one byte to the plugin archive, failed at `gh attestation verify` because no attestation exists for the changed digest ([run 39093707294](https://github.com/jellysin/plugin-lastfm/actions/runs/39093707294)). After `release.yml` moved onto `build-signed.yml`, the clean rehearsal through `verify-signed` passed ([run 39098760761](https://github.com/jellysin/plugin-lastfm/actions/runs/39098760761)), and in the tampered one both checks ran and failed: `gh attestation verify` found no attestation for the changed digest and `cosign verify-blob` reported an invalid signature ([run 39098762322](https://github.com/jellysin/plugin-lastfm/actions/runs/39098762322)). On `main` at `2045e94` the clean rehearsal passed again ([run 39106792075](https://github.com/jellysin/plugin-lastfm/actions/runs/39106792075)), the tampered one failed at `verify-signed` ([run 39106795372](https://github.com/jellysin/plugin-lastfm/actions/runs/39106795372)), and a final clean run left the latest rehearsal green ([run 39107015886](https://github.com/jellysin/plugin-lastfm/actions/runs/39107015886)). None of these runs creates a tag, release or draft. The pinned Praetor audit (engine `8c99fd7`) passes HISS-11 with no exception: it measures SLSA Build Level 3 from the reusable build, cosign signing and an SBOM step; it reads the workflow files and does not check published attestations. The first signed release tag repeats the same `verify-signed` check in `release.yml` before anything is published |
| Repository checks | PR/squash protections, pinned Actions, locks, Renovate and read-only defaults configured; candidate CI and all four CodeQL analyses passed |
| Publication and installation | The Plugin Repository lists 0.1.0.0 and 0.2.0.0; a fresh Jellyfin 12.2.0 server installed 0.2.0.0 from the catalog and listed it as `Active` after each of two restarts ([record](validation/release-0.2.0.md#catalog-install)) |
| Legacy migration | Separate plugin GUID and explicit uninstall/reconnect instructions; existing Last.fm history remains on Last.fm |
| Last.fm API terms | Closed as the project's own reading of the terms; the clarification is not sent (decisions of 2026-10-12 and 2026-10-17, below); no written Last.fm approval exists; no Last.fm images are offered (clause 5.1.8) |

The Last.fm API terms and the storage and display questions across independent
installations are documented in [API research](lastfm-api.md). On 2026-10-12 the
maintainer decided not to send the [clarification draft](lastfm-permission-request.md).
On 2026-10-17 the maintainer decided again not to write to Last.fm, after the
metadata providers and a Last.fm image provider had been added. Clause 5.1.8 of the
terms excludes images and artwork, so the image provider was removed before 0.2.0;
the biography and album text providers stay, with the link back to Last.fm.
The application key and shared secret identify the application, not a user, and the
plugin ships them in its DLL as the earlier Jellyfin Last.fm plugin did: its published
source holds both as constants
([`Resources/Strings.cs` at `6612157`](https://github.com/jesseward/jellyfin-plugin-lastfm/blob/66121574a59fc3def7a87cbe1e9c17e6ea645557/Jellyfin.Plugin.Lastfm/Resources/Strings.cs#L22-L26), read on 2026-10-12).
The terms row therefore records the project's own reading, not a confirmation from
Last.fm. Neither registration nor a successful API call is represented as separate
written permission for data redistribution. The decision does not change how user
session keys are stored; they stay protected on each server.

The detailed measured results belong in [validation.md](validation.md) and
[benchmarks/README.md](../benchmarks/README.md). A passing unit suite alone does not
close a host integration or release acceptance requirement.
