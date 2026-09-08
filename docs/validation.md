# Validation record

Local validation on 2026-09-08 used .NET SDK 10.0.400, Node 24.19.0, the repository
lockfiles and Jellyfin image `jellyfin/jellyfin:12.0@sha256:baba630419915985442f315f08b0cf46d9f4c8a0cc4bd38e94a6d35751dd5ef5`.
Python helper tests passed under 3.13.13 and 3.14.0. GitHub CI pins Node 24.20.0 and
Python 3.14.7 and repeats automated checks on proposed changes and release tags.

| Check | Observed result |
| --- | --- |
| .NET tests | 227 passed, none skipped |
| Unique production line coverage | 92.00% overall; 94.23% across API, configuration and transport |
| Restore, formatting, Release build | Locked restore; no formatting changes; zero warnings/errors |
| C# policy | 43 source files; no size or cognitive-complexity findings; analyzer self-tests passed |
| Dependency audits | No reported vulnerable dependencies in the five .NET projects or the npm development tree |
| Browser client unit tests | 16 passed, including byte limits, credential handling, immediate local logout and cancelled account requests |
| Browser scenarios | 18 passed: root/prefixed paths, both sign-in races, late history responses, reviewed operations, safe metadata, layout and performance |
| Accessibility | No axe violations in the tested signed-out and populated signed-in views at 320 and 1280 px |
| Frontend budget | Eight JavaScript modules total 11,963 bytes gzip; limit 150,000 bytes |
| Python helper tests | 16 passed: coverage validity and exact-tag/rebuild/upload-artifact comparison failures |
| Repository configuration | Actionlint, shared policy and the official Renovate JSON schema passed |

The real Docker smoke harness loaded the plugin as UID 1000 and verified embedded
resources, security headers, ordinary-user password and Quick Connect access,
anonymous/API-key/cross-origin rejection, caller isolation, configuration round
trips, music scanning, restricted-library access, disabled playback, root and
`/jellyfin` deployments, a forwarding proxy and restart persistence. It passed with
both an unconfigured build and a build containing the new project application.
The harness uses generated silent WAV fixtures and unique disposable storage.

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
is a separate assembly, excluded from the production package.

The unit suite covers per-item daily-limit retention, failed outbox persistence,
shutdown drain, dispatch-time now-playing expiry, disconnect/reconnect races,
application identity, lost protection keys, disabled users, ambiguous matching,
incomplete favourite collections, interrupted additions, complete history-date
retrieval, resumable imports, cache expiration and storage reserves.

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

The first production release still requires final hosted CI/security results,
independent rebuild comparison against selected upload artifacts, exact-tag
packaging/provenance and installation through the published catalog. The updated
native administrator application-override form still requires final live-browser
verification. No plugin 1.0.0 release is claimed by this validation record.
The separate Last.fm terms questions remain documented; no provider confirmation
is claimed. Existing repositories remain preserved until that release is verified. Native client
behavior beyond the tested host surfaces must not be inferred from unit coverage.
