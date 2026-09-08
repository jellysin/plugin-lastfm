# First-release acceptance

The version file describes the intended first release. It does not establish that
1.0.0 is ready. The initial implementation remains under review and no plugin
release has been published. Existing repositories and catalog files stay intact.

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
| Web page | 18 browser scenarios, 16 client tests, root/prefixed paths, immediate logout, safe rendering, review flows, axe, keyboard and performance budgets passed; final native admin form check pending |
| Large-library behavior | Real 10,000-track server with eight concurrent search/playback HTTP clients passed; rerun against final candidate |
| Callback performance | Production callbacks passed one/four-producer latency/allocation budgets with 256 linked accounts and zero dropped/failed writes |
| Release tooling | Shared tooling 1.0.1 published; 67 tests with two distinct plugin fixtures, SBOM inventory, strict validation and interrupted-upload retry |
| Reproducibility | Independent exact-commit rebuild comparison added; final committed candidate must pass |
| Repository checks | PR/squash protections, pinned Actions, dependency locks, Renovate and read-only defaults configured; final hosted checks pending |
| Publication and installation | Exact-tag production build, provenance, immutable release retry and published-catalog installation still pending |
| Legacy migration | Notices and archival intentionally follow a verified new release; unfinished modernization PRs remain unmerged |

The Last.fm API terms and unresolved distributed-storage/display questions are
documented in [API research](lastfm-api.md). The clarification draft is unsent;
neither registration nor a successful API call is represented as separate written
permission for data redistribution.

The detailed measured results belong in [validation.md](validation.md) and
[benchmarks/README.md](../benchmarks/README.md). A passing unit suite alone does not
close a host integration or release acceptance requirement.
