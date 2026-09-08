using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Playback;
using JellySin.Plugin.Lastfm.Storage;
using JellySin.Plugin.Lastfm.Transport;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

var root = Path.Combine(Path.GetTempPath(), "jellysin-benchmark-" + Guid.NewGuid().ToString("N"));
try
{
    using var store = new FileStateStore(root);
    var protection = new EphemeralDataProtectionProvider();
    var credentials = new ApplicationCredentialService(store, protection);
    using var accounts = new AccountService(store, new NoNetworkClient(), credentials, protection, TimeProvider.System);
    var outbox = new ScrobbleOutbox(store, accounts, new NoNetworkClient(), TimeProvider.System);
    var sessions = DispatchProxy.Create<ISessionManager, SessionEvents>();
    var events = (SessionEvents)sessions;
    var plugins = DispatchProxy.Create<IPluginManager, EmptyPlugins>();
    var music = Enumerable.Range(0, 256).Select(index => new PlaybackProgressEventArgs
    {
        Item = new Audio { Id = Guid.NewGuid(), Name = "Track " + index, Artists = ["Benchmark artist"], Album = "Benchmark album", RunTimeTicks = TimeSpan.FromMinutes(5).Ticks },
        Users = [new User("benchmark", "none", "none") { Id = Guid.NewGuid() }],
        PlaySessionId = "session-" + index,
        PlaybackPositionTicks = TimeSpan.FromSeconds(1).Ticks
    }).ToArray();
    var protector = protection.CreateProtector("JellySin.Lastfm.Account.v1");
    foreach (var item in music)
        await store.WriteAsync(item.Users.First().Id, "account", new AccountService.StoredAccount("benchmark", protector.Protect("synthetic-session"), false, true, Guid.NewGuid()), CancellationToken.None);
    var results = new List<CallbackResult>();
    foreach (var workers in new[] { 1, 4 })
    {
        var samples = new List<double>(20_480);
        var allocations = new List<double>(20 * workers);
        for (var round = 0; round < 21; round++)
        {
            using var playback = new PlaybackService(sessions, plugins, new PlaybackTracker(TimeProvider.System), outbox, accounts, TimeProvider.System, NullLogger<PlaybackService>.Instance);
            await playback.StartAsync(CancellationToken.None);
            if (music.Any(item => accounts.GetCaptureBinding(item.Users.First().Id) is null))
                throw new InvalidOperationException("Benchmark accounts must be actively linked; measuring rejection is invalid.");
            var starts = events.Handlers["PlaybackStart"];
            var progress = events.Handlers["PlaybackProgress"];
            foreach (var item in music) starts(null, item);
            var timings = Enumerable.Range(0, workers).Select(_ => new double[1024 / workers]).ToArray();
            var allocated = new double[workers];
            Parallel.For(0, workers, worker =>
            {
                var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                for (var index = 0; index < timings[worker].Length; index++)
                {
                    var start = Stopwatch.GetTimestamp();
                    progress(null, music[(index * workers + worker) % music.Length]);
                    timings[worker][index] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
                }
                allocated[worker] = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / (double)timings[worker].Length;
            });
            if (round > 0) { samples.AddRange(timings.SelectMany(values => values)); allocations.AddRange(allocated); }
            await playback.StopAsync(CancellationToken.None);
            if (playback.GetStatus() is { DroppedSnapshots: not 0 } or { FailedWrites: not 0 }) throw new InvalidOperationException("Benchmark overflowed; results are invalid.");
        }
        samples.Sort();
        results.Add(new(workers, samples.Count, samples[(int)(samples.Count * .50)], samples[(int)(samples.Count * .95)],
            samples[(int)(samples.Count * .99)], allocations.Average()));
    }
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        scenario = "Production playback callbacks, 256 session identities, one and four producer threads, 20 rounds after warmup; observation worker active",
        runtime = RuntimeInformation.FrameworkDescription,
        os = RuntimeInformation.OSDescription,
        processorCount = Environment.ProcessorCount,
        linkedAccounts = music.Length,
        results,
        budgets = new { p95Microseconds = 50, p99Microseconds = 200, allocatedBytesPerCallback = 1024 },
        droppedSnapshots = 0,
        failedWrites = 0
    }, new JsonSerializerOptions { WriteIndented = true }));
    if (results.Any(result => result.P95Microseconds > 50 || result.P99Microseconds > 200 || result.AverageAllocatedBytesPerCallback > 1024))
        throw new InvalidOperationException("Production callback latency or allocation budget exceeded.");
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

public sealed record CallbackResult(int ProducerThreads, int Samples, double P50Microseconds, double P95Microseconds,
    double P99Microseconds, double AverageAllocatedBytesPerCallback);

public class SessionEvents : DispatchProxy
{
    public Dictionary<string, EventHandler<PlaybackProgressEventArgs>> Handlers { get; } = [];
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod?.Name ?? string.Empty;
        if (name.StartsWith("add_", StringComparison.Ordinal) && args?[0] is EventHandler<PlaybackProgressEventArgs> handler) Handlers[name[4..]] = handler;
        if (name.StartsWith("remove_", StringComparison.Ordinal)) Handlers.Remove(name[7..]);
        return null;
    }
}

public class EmptyPlugins : DispatchProxy
{
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name == "get_Plugins" ? Array.Empty<LocalPlugin>() : null;
}

public sealed class NoNetworkClient : ILastfmClient
{
    public Task<JsonDocument> CallAsync(string method, IReadOnlyDictionary<string, string> parameters, string? sessionKey, RequestPriority priority, CancellationToken cancellationToken)
        => throw new InvalidOperationException("The playback callback benchmark must never access the network.");
}
