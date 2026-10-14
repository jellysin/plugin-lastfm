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
CI first runs the harness with `--self-test`, which requires each budget to refuse a
planted result just above it and to accept one at the budget.

Measured on 2026-10-11 for `5489d0a`. The harness runs in one process against the
Jellyfin 12.0.0 interfaces the plugin is compiled with; no server takes part, so
these numbers do not depend on the server release.

| Run | Producers | Median | p95 | p99 | Mean allocation |
| --- | ---: | ---: | ---: | ---: | ---: |
| Hosted CI: .NET 10.0.12, Ubuntu 24.04.5, 4 logical processors | 1 | 1.0 μs | 1.6 μs | 2.2 μs | 260.8 bytes/callback |
| | 4 | 1.5 μs | 2.0 μs | 2.8 μs | 276.1 bytes/callback |
| Local SDK container: .NET 10.0.12, Ubuntu 24.04.5, 5 logical processors | 1 | 0.3 μs | 0.4 μs | 1.7 μs | 260.8 bytes/callback |
| | 4 | 0.4 μs | 1.1 μs | 1.8 μs | 276.1 bytes/callback |

All scenarios recorded zero dropped observations and failed writes. These are
callback measurements, not whole-server latency or improvement claims against an
inherited implementation. CI retains its own runtime, OS, processor count and
measurement JSON. Compare equivalent workloads on the same runtime and hardware.

## Real library workload

```sh
python tools/library_load.py --plugin src/JellySin.Plugin.Lastfm/bin/Release/net10.0/JellySin.Plugin.Lastfm.dll --tracks 10000 --server newest
```

The harness starts a fresh pinned Jellyfin 12 container with two CPUs and 1,536 MiB
memory, generates and scans 10,000 silent tracks, then runs eight parallel HTTP
clients: four ordinary-user plugin searches and four playback-progress producers.
Each request type records 200 samples after warmup. Searches must return the exact
generated track, delivery state must remain empty and healthy, and HTTP p95 must
stay below 1,000 ms. No Last.fm account is connected or external music API called.
Only this harness's uniquely owned container, volumes and fixtures are cleaned up.

Measured on 2026-10-11 for `5489d0a`, one run each. CI enforces the budget on
Jellyfin 12.2.0 only; the 12.0.0 row is a local run for comparison on the same host.

| Run | Server | Scan | Request | Median | p95 | p99 |
| --- | --- | ---: | --- | ---: | ---: | ---: |
| Hosted CI | 12.2.0 | 12.6 s | Plugin library search | 207.9 ms | 323.0 ms | 437.3 ms |
| | | | Native playback progress | 191.4 ms | 398.5 ms | 534.0 ms |
| Local | 12.2.0 | 8.2 s | Plugin library search | 35.8 ms | 139.0 ms | 176.4 ms |
| | | | Native playback progress | 37.0 ms | 187.5 ms | 324.3 ms |
| Local | 12.0.0 | 10.4 s | Plugin library search | 27.7 ms | 174.5 ms | 190.6 ms |
| | | | Native playback progress | 47.6 ms | 463.7 ms | 626.2 ms |

The local host is an AMD Ryzen 9 9950X3D that ran other work at the same time. An
earlier local run on 12.2.0 the same day measured 33.3/171.6/299.5 ms for search and
24.4/315.0/351.3 ms for playback, so single runs differ by this much and the table
shows no difference between the two servers. All runs stayed below the budget.

These numbers include Docker networking, HTTP, authorization and Jellyfin's own
work. They do not describe the plugin callback alone. The playback requests are
paused and use an unconnected test account, so this workload cannot send scrobbles.

## Favourite synchronization

```sh
python tools/favourites_load.py --plugin src/JellySin.Plugin.Lastfm/bin/Release/net10.0/JellySin.Plugin.Lastfm.dll --tracks 10000 --server newest
```

The harness generates and scans the same library as the real library workload, in
a container with the same limits. The Last.fm stand-in of the page check serves
every generated track but the last as loved, 200 to a page, under a made-up
application. One ordinary user connects, and the harness times four phases:

- **Initial:** favourites are turned on, and every loved track becomes a favourite.
- **Unchanged:** a synchronization with nothing changed on either side.
- **One favourite:** the last track is marked a favourite in Jellyfin. The phase ends
  when the love has reached the stand-in and the reconciliation has completed.
- **One removal:** the stand-in stops serving the oldest loved track. The phase ends
  when the plugin proposes removing that favourite from Jellyfin and the
  reconciliation has completed. The review then applies the proposal in one request.

A run reconciles at most 200 track ids, so each phase triggers runs back to back
until it is done, and records its runs and the loved pages the stand-in served.
**Time to the love** is how long after the mark the stand-in received the love, and
**time to the proposal** how long after the drop the removal was proposed.
**Review** is the request that applies the proposal; it reads the loved collection
again to confirm the removal before it changes the favourite.
No real Last.fm account, application or credential takes part.

Measured on 2026-10-14 on Jellyfin 12.2.0, one run each, on the local host of the
table above (other work running, load average 6 to 15 at the start of each run).
**Before** is `09423ea`. **Reuse** is this change applied to `2979a9d`; the commit
between the two changes only the held-play warning.

| Tracks | Phase | Before | Before pages | Reuse | Reuse pages |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1,000 | Initial | 41.8 s | 30 | 31.8 s | 10 |
| | Unchanged | 23.5 s | 30 | 3.7 s | 5 |
| | One favourite | 28.9 s | 30 | 6.7 s | 5 |
| | Time to the love | 9.7 s | | 3.8 s | |
| 10,000 | Initial | 2,267.0 s | 2,550 | 348.7 s | 100 |
| | Unchanged | 2,152.8 s | 2,550 | 68.5 s | 50 |
| | One favourite | 2,067.2 s | 2,550 | 80.2 s | 50 |
| | Time to the love | 213.1 s | | 21.5 s | |

The removal phase and its review were measured later the same day, one run each, with
the harness of this section: **Before** is again `09423ea`, **Reuse** is `b07a4c6`,
which carries the change. The 10,000-track before run started at a load average of 4
and ended at 37.

| Tracks | Phase | Before | Before pages | Reuse | Reuse pages |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1,000 | One removal | 32.8 s | 30 | 13.2 s | 10 |
| | Time to the proposal | 28.1 s | | 12.4 s | |
| | Review | 4.0 s | 6 | 0.05 s | 1 |
| 10,000 | One removal | 2,875.1 s | 2,550 | 100.9 s | 100 |
| | Time to the proposal | 777.0 s | | 54.2 s | |
| | Review | 75.9 s | 51 | 0.1 s | 1 |

The review reads page 1 and, before the change, the whole collection again; with
reuse it keeps the collection the run read, because page 1 has not changed since.

Before, every run read the whole loved collection and matched every loved track
against the library again. With reuse, a run reads page 1 and keeps the earlier
collection while that page is unchanged. It keeps a track's library match until
the library or the user's access changes. The initial phase writes favourites, and
each write changes the library, so its runs still match again.

In production, runs do not follow one another back to back. The periodic refresh
starts one every five minutes, so at 10,000 loved tracks a change can wait about
50 refreshes for the run whose 200 ids hold it. Reuse makes each run cheaper but
does not change that wait.

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
