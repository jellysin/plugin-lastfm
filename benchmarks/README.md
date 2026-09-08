# Playback callback measurement

Run `dotnet run --project benchmarks/JellySin.Plugin.Lastfm.Benchmarks -c Release`.
The harness invokes the production Jellyfin event callback with 256 concurrent
session identities. It measures 20,480 progress events after a warmup round,
with the observation worker active, and fails if observations overflow or writes
fail. Host entities are prepared outside the measured region. No library or
network implementation is injected into the callback.

Measured on 2026-09-08 using .NET 10.0.11, Windows build 26200 and 24 logical
processors:

| Metric | Result |
| --- | ---: |
| Median callback | 0.5 microseconds |
| 95th percentile | 1.6 microseconds |
| 99th percentile | 5.9 microseconds |
| Mean allocation | 209.95 bytes/callback |
| Dropped observations / failed writes | 0 / 0 |

This is a local callback microbenchmark, not a claim about whole-server latency
or Last.fm response speed. It does not measure library matching, network traffic,
or disk flush throughput. The callback's inputs are existing Jellyfin entities;
library size does not cause a query on this path. Compare only equivalent runs
on the same machine/runtime, and retain the benchmark's JSON output in CI.
