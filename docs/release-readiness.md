# Release readiness

The plugin is in development; no published release is available. The candidate
validation below records observed checks, not approval for production publication.

## Evidence and remaining work

| Requirement | Evidence / outstanding verification |
| --- | --- |
| Fresh Jellyfin 12 implementation | Independent history, GUID and namespace; .NET 10; no 10.11 compatibility branch |
| Account connection | Registered project application; real ordinary-user password/Quick Connect and Last.fm browser grant passed |
| Credential protection | Protected sessions, authorization headers, redaction, per-user auth cancellation, disabled users and application-change regressions passed |
| Playback and durable delivery | One explicitly authorized live scrobble passed; daily-limit retention, account transitions, enqueue retry and shutdown drain regressions passed |
| Favourite reconciliation | Additive merge, ambiguous identities, reviewed removals, intervening edits and interrupted-addition recovery tested; native revision protection passed |
| Native metadata | Real artist, album and track refreshes, Last.fm links/tags and locked-field preservation verified; native-copy quota/reserve tests passed |
| History and charts | Live views passed; bounded complete-date retrieval/resumable application tested; real native stale-write protection and deliberate resets passed |
| Personalized discovery | Real local/external results and unknown-seed behavior verified; private cache-first/expiration tests passed |
| Native similarity | Actual provider picker and Last.fm-influenced native ranking verified; public data only |
| Private generated playlists | Actual ownership, regeneration, injected native write failure, durable restart recovery and retry passed; reviewed cancellation tested |
| Storage and lifecycle | Atomic updates, cancellation, byte/document limits, durable reserves and orphan cleanup regressions passed |
| Web page | 18 browser scenarios, 16 client tests, root/prefixed paths, immediate logout, safe rendering, review flows, axe, keyboard and performance budgets passed; final native admin form/live read-only checks passed |
| Large-library behavior | Final real 10,000-track server with eight concurrent search/playback HTTP clients passed locally and in hosted CI |
| Callback performance | Production callbacks passed one/four-producer latency/allocation budgets with 256 linked accounts and zero dropped/failed writes |
| Release tooling | The tooling revision tested on 2026-09-08 passed 67 tests with two distinct plugin fixtures, SBOM inventory, strict validation and interrupted-upload retry |
| Reproducibility | Two independent checkouts matched the actual selected ZIP, release metadata, SBOM and checksums locally and in hosted CI; production tag repeats the gate with embedded credentials |
| Repository checks | PR/squash protections, pinned Actions, locks, Renovate and read-only defaults configured; candidate CI and all four CodeQL analyses passed |
| Publication and installation | No plugin release is available in the Plugin Repository |
| Legacy migration | Separate plugin GUID and explicit uninstall/reconnect instructions; existing Last.fm history remains on Last.fm |

The Last.fm API terms and unresolved distributed-storage/display questions are
documented in [API research](lastfm-api.md). Neither registration nor a successful
API call is represented as separate written
permission for data redistribution.

The detailed measured results belong in [validation.md](validation.md) and
[benchmarks/README.md](../benchmarks/README.md). A passing unit suite alone does not
close a host integration or release acceptance requirement.
