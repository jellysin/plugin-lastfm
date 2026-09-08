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
    ScrobbleOutbox outbox, AccountService accounts, TimeProvider clock, ILogger<PlaybackService> logger) : BackgroundService
{
    public static readonly Guid LegacyPluginId = new("5e7fe7f0-b048-429e-a431-b1a7e69c930d");
    private readonly Channel<PlaybackSnapshot?> _snapshots = Channel.CreateBounded<PlaybackSnapshot?>(new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<EligibleListen> _nowPlaying = Channel.CreateBounded<EligibleListen>(new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private long _dropped;
    private long _failedWrites;
    private bool _legacyPresent;
    private int _retryCursor;

    public PlaybackRuntimeStatus GetStatus() => new(_legacyPresent, Interlocked.Read(ref _dropped), Interlocked.Read(ref _failedWrites));

    public int PendingPersistence(Guid userId) => tracker.PendingCount(userId);

    public bool TryTakeNowPlaying(out EligibleListen? listen)
    {
        while (_nowPlaying.Reader.TryRead(out listen))
            if (IsCurrentNowPlaying(listen)) return true;
        listen = null;
        return false;
    }

    public bool IsCurrentNowPlaying(EligibleListen listen) => tracker.IsCurrent(listen) && Plugin.Instance?.ListeningEnabled != false
        && accounts.GetCaptureBinding(listen.UserId)?.CaptureGeneration == listen.CaptureGeneration
        && listen.PluginGeneration == (Plugin.Instance?.ListeningGeneration ?? 0);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await accounts.InitializeCapturesAsync(cancellationToken).ConfigureAwait(false);
        _legacyPresent = plugins.Plugins.Any(plugin => plugin.Id == LegacyPluginId && (plugin.Instance is not null || plugin.IsEnabledAndSupported));
        if (_legacyPresent) logger.LogWarning("JellySin listening is paused because the legacy Last.fm plugin is installed. Remove it and restart Jellyfin.");
        sessions.PlaybackStart += OnStart;
        sessions.PlaybackProgress += OnProgress;
        sessions.PlaybackStopped += OnStop;
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
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
        using var timerCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var retryTimer = QueueRetryTicksAsync(timerCancellation.Token);
        try
        {
            await foreach (var snapshot in _snapshots.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    if (snapshot is null) { await RetryPersistenceAsync(stoppingToken).ConfigureAwait(false); continue; }
                    if (Plugin.Instance?.ListeningEnabled == false || accounts.GetCaptureBinding(snapshot.UserId) is not { } binding
                        || binding.AccountGeneration != snapshot.AccountGeneration || binding.CaptureGeneration != snapshot.CaptureGeneration
                        || snapshot.PluginGeneration != (Plugin.Instance?.ListeningGeneration ?? 0)) continue;
                    var update = tracker.Observe(snapshot);
                    if (update.Scrobble is not null) await PersistAsync(update.Scrobble, stoppingToken).ConfigureAwait(false);
                    if (update.NowPlaying is not null) _nowPlaying.Writer.TryWrite(update.NowPlaying);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    Interlocked.Increment(ref _failedWrites);
                    logger.LogError("Listening observation could not be persisted ({ErrorType}). Check JellySin status and storage.", exception.GetType().Name);
                }
            }
            await RetryPersistenceAsync(stoppingToken, int.MaxValue).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await timerCancellation.CancelAsync().ConfigureAwait(false); await retryTimer.ConfigureAwait(false); }
    }

    private async Task QueueRetryTicksAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                if (!_snapshots.Writer.TryWrite(null) && _snapshots.Reader.Completion.IsCompleted) break;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task PersistAsync(EligibleListen listen, CancellationToken cancellationToken)
    {
        if (accounts.GetCaptureBinding(listen.UserId) is { } binding && binding.AccountGeneration != listen.AccountGeneration)
        { tracker.Acknowledge(listen.OccurrenceId); return; }
        await outbox.EnqueueAsync(listen, cancellationToken).ConfigureAwait(false);
        tracker.Acknowledge(listen.OccurrenceId);
    }

    private async Task RetryPersistenceAsync(CancellationToken cancellationToken, int limit = 8)
    {
        var pending = tracker.Pending();
        if (pending.Count == 0) return;
        for (var index = 0; index < Math.Min(limit, pending.Count); index++)
        {
            var listen = pending[_retryCursor++ % pending.Count];
            try { await PersistAsync(listen, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (OperationCanceledException) { tracker.Acknowledge(listen.OccurrenceId); }
            catch (Exception exception)
            {
                Interlocked.Increment(ref _failedWrites);
                logger.LogWarning("Listening persistence will retry ({ErrorType}).", exception.GetType().Name);
            }
        }
        _retryCursor %= pending.Count;
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
            var duration = (audio.RunTimeTicks ?? 0) / (double)TimeSpan.TicksPerSecond;
            if (duration is <= 30 or > int.MaxValue) return;
            var track = new MusicTrack(audio.Id, artist, audio.Name, audio.Album?.Length <= 4096 ? audio.Album : null,
                Guid.TryParse(mbid, out var identifier) ? identifier.ToString() : null, duration);
            var timestamp = clock.GetTimestamp();
            var utc = clock.GetUtcNow();
            foreach (var user in args.Users.Take(32))
            {
                var binding = accounts.GetCaptureBinding(user.Id);
                if (binding is null) continue;
                var snapshot = new PlaybackSnapshot(sessionId, user.Id, track, signal, args.PlaybackPositionTicks ?? 0, args.IsPaused, false, timestamp, utc, binding.AccountGeneration, binding.CaptureGeneration, Plugin.Instance?.ListeningGeneration ?? 0);
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
