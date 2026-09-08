# Performance measurements

Run the production callback harness with:

```sh
dotnet run --project benchmarks/JellySin.Plugin.Lastfm.Benchmarks -c Release
```

It prepares 256 linked synthetic accounts and session identities, then measures
20,480 progress callbacks per scenario after warmup, with one and four producer
threads and the observation worker active. Setup, account persistence and host
entities are outside the measurement. The harness rejects unlinked accounts and
fails on queue overflow, failed writes, p95 above 50 microseconds, p99 above 200
microseconds or allocation above 1,024 bytes per callback. These are regression
budgets; no network, disk or library query runs inside the playback callback.

Measured locally on 2026-09-08 with .NET 10.0.11, Windows build 26200 and 24 logical
processors:

| Producers | Median | p95 | p99 | Mean allocation |
| --- | ---: | ---: | ---: | ---: |
| 1 | 0.3 μs | 1.1 μs | 1.7 μs | 276.1 bytes/callback |
| 4 | 0.4 μs | 2.1 μs | 4.0 μs | 276.1 bytes/callback |

Both scenarios recorded zero dropped observations and failed writes. These are
callback measurements, not whole-server latency or improvement claims against an
inherited implementation. CI retains its own runtime, OS, processor count and
measurement JSON. Compare equivalent workloads on the same runtime and hardware.

## Real library workload

```sh
python tools/library_load.py --plugin src/JellySin.Plugin.Lastfm/bin/Release/net10.0/JellySin.Plugin.Lastfm.dll --tracks 10000
```

The harness starts a fresh pinned Jellyfin 12 container with two CPUs and 1,536 MiB
memory, generates and scans 10,000 silent tracks, then runs eight parallel HTTP
clients: four ordinary-user plugin searches and four playback-progress producers.
Each request type records 200 samples after warmup. Searches must return the exact
generated track, delivery state must remain empty and healthy, and HTTP p95 must
stay below 1,000 ms. No Last.fm account is connected or external music API called.
Only this harness's uniquely owned container, volumes and fixtures are cleaned up.

The 2026-09-08 local run scanned all 10,000 tracks in 22.7 seconds:

| Request | Median | p95 | p99 |
| --- | ---: | ---: | ---: |
| Plugin library search | 174.7 ms | 287.0 ms | 350.8 ms |
| Native playback progress | 143.3 ms | 356.3 ms | 461.8 ms |

These numbers include Docker networking, HTTP, authorization and Jellyfin's own
work. They do not describe the plugin callback alone. The playback requests are
paused and use an unconnected test account, so this workload cannot send scrobbles.

## Browser budgets

Playwright measures the plugin page at 320 and 1,280 pixels wide, with Chromium
CPU slowed by a factor of four, 80 ms fixture latency per response and a 200-track
history page. Network bandwidth is not throttled. It requires LCP below 2,500 ms,
CLS below 0.1 and the worst observed interaction below 200 ms. The latter is a
conservative laboratory responsiveness check, not a field INP claim; browser event
timing does not report interactions below its configured 16 ms threshold.

The final local Chromium 153 run recorded LCP of 428/428 ms, CLS of zero and worst
reported interactions of 40/56 ms for mobile/desktop respectively. Delayed-response
tests exposed layout shifts; stable, keyboard-accessible result panes resolved
them. Test artifacts record each subsequent run. The JavaScript gzip budget is
150 KB; the current eight modules are approximately 12 KB. Accessibility checks separately cover axe,
keyboard navigation, focus recovery and narrow-screen overflow.

The measurement definitions follow the browser's
[Event Timing API](https://developer.mozilla.org/en-US/docs/Web/API/PerformanceEventTiming),
[CLS session windows](https://web.dev/articles/cls) and
[CPU throttling control](https://chromedevtools.github.io/devtools-protocol/tot/Emulation/#method-setCPUThrottlingRate).
