# Release readiness

The plugin is in development; no published release is available. The candidate
validation below records observed checks, not approval for production publication.

The plugin is compiled against Jellyfin 12.0.0 and supports Jellyfin 12 from there
up. The automated server checks run on Jellyfin 12.0.0 and 12.2.0 on every change, and
the native fixture also on 12.1.0, the first release with the playlist visibility rule.
Rows marked "12.0 only" rest on observations made on 2026-09-08 with the project
application and a live Last.fm account on the 12.0 image; they have not been repeated
on 12.2 and are outstanding for that server.

## Evidence and remaining work

| Requirement | Evidence / outstanding verification |
| --- | --- |
| Fresh Jellyfin 12 implementation | Independent history, GUID and namespace; .NET 10; no 10.11 compatibility branch; one build loaded on Jellyfin 12.0.0 and 12.2.0 |
| Account connection | Registered project application; real ordinary-user password/Quick Connect passed on 12.0.0 and 12.2.0; Last.fm browser grant passed (12.0 only) |
| Credential protection | Protected sessions, authorization headers, redaction, per-user auth cancellation, disabled users and application-change regressions passed |
| Playback and durable delivery | One explicitly authorized live scrobble passed (12.0 only); daily-limit retention, account transitions, enqueue retry and shutdown drain regressions passed |
| Favourite reconciliation | Additive merge, ambiguous identities, reviewed removals, intervening edits and interrupted-addition recovery tested; native revision protection passed |
| History and charts | Live views passed (12.0 only); bounded complete-date retrieval/resumable application tested; real native stale-write protection and deliberate resets passed on 12.0.0 and 12.2.0 |
| Personalized discovery | Real local/external results and unknown-seed behavior verified (12.0 only); private cache-first/expiration tests passed; library matching gave the same recorded answers on 12.0.0, 12.1.0 and 12.2.0 |
| Private generated playlists | Actual ownership, regeneration, injected native write failure, durable restart recovery and retry passed on 12.0.0 and 12.2.0; reviewed cancellation tested; visibility under the owner's parental limit recorded for 12.0.0, 12.1.0 and 12.2.0 |
| Storage and lifecycle | Atomic updates, cancellation, byte/document limits, durable reserves and orphan cleanup regressions passed |
| Web page | 18 browser scenarios, 16 client tests, root/prefixed paths, immediate logout, safe rendering, review flows, axe, keyboard and performance budgets passed; final native admin form/live read-only checks passed (12.0 only) |
| Large-library behavior | Real 10,000-track Jellyfin 12.2.0 server with eight concurrent search/playback HTTP clients passed locally and in hosted CI; a local run on 12.0.0 passed the same budget |
| Callback performance | Production callbacks passed one/four-producer latency/allocation budgets with 256 linked accounts and zero dropped/failed writes |
| Release tooling | The tooling revision tested on 2026-09-08 passed 67 tests with two distinct plugin fixtures, SBOM inventory, strict validation and interrupted-upload retry |
| Reproducibility | Two independent checkouts matched the actual selected ZIP, release metadata, SBOM and checksums locally and in hosted CI; production tag repeats the gate with embedded credentials |
| Repository checks | PR/squash protections, pinned Actions, locks, Renovate and read-only defaults configured; candidate CI and all four CodeQL analyses passed |
| Publication and installation | No plugin release is available in the Plugin Repository |
| Legacy migration | Separate plugin GUID and explicit uninstall/reconnect instructions; existing Last.fm history remains on Last.fm |

The Last.fm API terms and unresolved distributed-storage/display questions are
documented in [API research](lastfm-api.md). The clarification draft is unsent;
neither registration nor a successful API call is represented as separate written
permission for data redistribution.

The detailed measured results belong in [validation.md](validation.md) and
[benchmarks/README.md](../benchmarks/README.md). A passing unit suite alone does not
close a host integration or release acceptance requirement.
