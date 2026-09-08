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
    var samples = new List<double>(20_480);
    var allocations = new List<double>(20);
    var music = Enumerable.Range(0, 256).Select(index => new PlaybackProgressEventArgs
    {
        Item = new Audio { Id = Guid.NewGuid(), Name = "Track " + index, Artists = ["Benchmark artist"], Album = "Benchmark album", RunTimeTicks = TimeSpan.FromMinutes(5).Ticks },
        Users = [new User("benchmark", "none", "none") { Id = Guid.NewGuid() }],
        PlaySessionId = "session-" + index,
        PlaybackPositionTicks = TimeSpan.FromSeconds(1).Ticks
    }).ToArray();
    for (var round = 0; round < 21; round++)
    {
        using var playback = new PlaybackService(sessions, plugins, new PlaybackTracker(TimeProvider.System), outbox, TimeProvider.System, NullLogger<PlaybackService>.Instance);
        await playback.StartAsync(CancellationToken.None);
        var starts = events.Handlers["PlaybackStart"];
        var progress = events.Handlers["PlaybackProgress"];
        foreach (var item in music) starts(null, item);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1024; index++)
        {
            var start = Stopwatch.GetTimestamp();
            progress(null, music[index % music.Length]);
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            if (round > 0) samples.Add(elapsed);
        }
        if (round > 0) allocations.Add((GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / 1024d);
        await playback.StopAsync(CancellationToken.None);
        if (playback.GetStatus() is { DroppedSnapshots: not 0 } or { FailedWrites: not 0 }) throw new InvalidOperationException("Benchmark overflowed; results are invalid.");
    }
    samples.Sort();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        scenario = "Playback progress callback, 256 concurrent sessions, 20 measured rounds after warmup; in-memory host entities; worker active; no library query",
        runtime = RuntimeInformation.FrameworkDescription,
        os = RuntimeInformation.OSDescription,
        processorCount = Environment.ProcessorCount,
        samples = samples.Count,
        p50Microseconds = samples[(int)(samples.Count * .50)],
        p95Microseconds = samples[(int)(samples.Count * .95)],
        p99Microseconds = samples[(int)(samples.Count * .99)],
        averageAllocatedBytesPerCallback = allocations.Average(),
        droppedSnapshots = 0,
        failedWrites = 0
    }, new JsonSerializerOptions { WriteIndented = true }));
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }

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
