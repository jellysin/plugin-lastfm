using System.Threading.Channels;
using JellySin.Plugin.Lastfm.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Playback;

public sealed record PlaybackRuntimeStatus(bool LegacyPluginDetected, long DroppedSnapshots, long FailedWrites);

public sealed class PlaybackService(ISessionManager sessions, IPluginManager plugins, PlaybackTracker tracker,
    ScrobbleOutbox outbox, TimeProvider clock, ILogger<PlaybackService> logger) : BackgroundService
{
    public static readonly Guid LegacyPluginId = new("5e7fe7f0-b048-429e-a431-b1a7e69c930d");
    private readonly Channel<PlaybackSnapshot> _snapshots = Channel.CreateBounded<PlaybackSnapshot>(new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<EligibleListen> _nowPlaying = Channel.CreateBounded<EligibleListen>(new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private long _dropped;
    private long _failedWrites;
    private bool _legacyPresent;

    public PlaybackRuntimeStatus GetStatus() => new(_legacyPresent, Interlocked.Read(ref _dropped), Interlocked.Read(ref _failedWrites));

    public bool TryTakeNowPlaying(out EligibleListen? listen) => _nowPlaying.Reader.TryRead(out listen);

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _legacyPresent = plugins.Plugins.Any(plugin => plugin.Id == LegacyPluginId && (plugin.Instance is not null || plugin.IsEnabledAndSupported));
        if (_legacyPresent) logger.LogWarning("JellySin listening is paused because the legacy Last.fm plugin is installed. Remove it and restart Jellyfin.");
        sessions.PlaybackStart += OnStart;
        sessions.PlaybackProgress += OnProgress;
        sessions.PlaybackStopped += OnStop;
        return base.StartAsync(cancellationToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        Detach();
        _snapshots.Writer.TryComplete();
        try
        {
            if (ExecuteTask is not null) await ExecuteTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { await base.StopAsync(cancellationToken).ConfigureAwait(false); }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var snapshot in _snapshots.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    var update = tracker.Observe(snapshot);
                    if (update.Scrobble is not null) await outbox.EnqueueAsync(update.Scrobble, stoppingToken).ConfigureAwait(false);
                    if (update.NowPlaying is not null) _nowPlaying.Writer.TryWrite(update.NowPlaying);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    Interlocked.Increment(ref _failedWrites);
                    logger.LogError("Listening observation could not be persisted ({ErrorType}). Check JellySin status and storage.", exception.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private void OnStart(object? sender, PlaybackProgressEventArgs args) => Capture(args, PlaybackSignal.Start);
    private void OnProgress(object? sender, PlaybackProgressEventArgs args) => Capture(args, PlaybackSignal.Progress);
    private void OnStop(object? sender, PlaybackStopEventArgs args) => Capture(args, PlaybackSignal.Stop);

    private void Capture(PlaybackProgressEventArgs args, PlaybackSignal signal)
    {
        try
        {
            if (_legacyPresent || Plugin.Instance?.ListeningEnabled == false || args.IsAutomated || args.Item is not Audio audio) return;
            var artist = audio.Artists.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(audio.Name) || artist.Length > 1024 || audio.Name.Length > 1024) return;
            var sessionId = args.PlaySessionId ?? args.Session?.Id ?? args.DeviceId;
            if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 256) return;
            audio.ProviderIds.TryGetValue("MusicBrainzTrack", out var mbid);
            var track = new MusicTrack(audio.Id, artist, audio.Name, audio.Album, mbid, (audio.RunTimeTicks ?? 0) / (double)TimeSpan.TicksPerSecond);
            var timestamp = clock.GetTimestamp();
            var utc = clock.GetUtcNow();
            foreach (var user in args.Users.Take(32))
            {
                var snapshot = new PlaybackSnapshot(sessionId, user.Id, track, signal, args.PlaybackPositionTicks ?? 0, args.IsPaused, false, timestamp, utc);
                if (!_snapshots.Writer.TryWrite(snapshot)) Interlocked.Increment(ref _dropped);
            }
        }
        catch (Exception) { Interlocked.Increment(ref _dropped); }
    }

    private void Detach()
    {
        sessions.PlaybackStart -= OnStart;
        sessions.PlaybackProgress -= OnProgress;
        sessions.PlaybackStopped -= OnStop;
    }

    public override void Dispose() { Detach(); base.Dispose(); }
}
