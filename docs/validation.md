# Validation record

Local validation on 2026-09-08 used the pinned .NET 10 SDK, the repository lockfiles
and Jellyfin image `jellyfin/jellyfin:12.0@sha256:baba630419915985442f315f08b0cf46d9f4c8a0cc4bd38e94a6d35751dd5ef5`.
GitHub CI repeats the automated checks on each proposed change and release tag.

| Check | Observed result |
| --- | --- |
| .NET tests | 163 passed, none skipped |
| Unique production line coverage | 89.96% overall; 93.06% across API, configuration and transport |
| Restore, formatting, Release build | Locked restore; no formatting changes; zero warnings/errors |
| C# policy | 37 source files; no size or cognitive-complexity findings; analyzer self-tests passed |
| Dependency audits | No reported vulnerable dependencies in the four .NET projects or the npm development tree |
| Browser client unit tests | 13 passed, including byte limits, credential handling and cancelled account requests |
| Browser scenarios | 9 passed: root/prefixed paths, authentication, reviewed operations, safe metadata and layout |
| Accessibility | No axe violations in the tested signed-out and populated signed-in views at 320 and 1280 px |
| Frontend budget | Eight JavaScript modules total approximately 10.7 KB gzip; limit 150 KB |
| Coverage-tool tests | 11 passed, including duplicate lines, stale reports, invalid XML and failed thresholds |
| Secret scan | Staged source passed Gitleaks with redaction; build credentials and private test state are ignored |

The real Docker smoke harness loaded the plugin as UID 1000 and verified embedded
resources, security headers, ordinary-user password and Quick Connect access,
anonymous/API-key/cross-origin rejection, caller isolation, configuration round
trips, music scanning, restricted-library access, disabled playback, root and
`/jellyfin` deployments, a forwarding proxy and restart persistence. It passed with
both an unconfigured build and a build containing the new project application.
The harness uses generated silent WAV fixtures and unique disposable storage.

The project application completed the Last.fm browser token/session flow. Live
history, period charts/statistics and discovery returned data. One explicitly
authorized 65-second generated fixture was observed for 35.16 real seconds and
submitted through Jellyfin playback callbacks and the durable outbox. Last.fm
returned exactly one matching completed recent entry; pending/blocked submissions,
dropped snapshots and failed writes were all zero. Further scrobbling was disabled
on the isolated account afterward. This observation does not establish exactly-once
delivery under every network failure. Private history and credentials are not
published with validation artifacts.

Callback measurements and their workload limits are in
[benchmarks/README.md](../benchmarks/README.md). They are local measurements, not
claims of improvement over an inherited implementation.

## Remaining release checks

The first production release still requires hosted CI/security results, exact-tag
packaging and provenance verification, installation through the published catalog,
and Last.fm confirmation of the distribution/storage/display arrangements. Existing
repositories remain preserved until that release is verified. Native client
behavior beyond the tested host surfaces must not be inferred from unit coverage.
