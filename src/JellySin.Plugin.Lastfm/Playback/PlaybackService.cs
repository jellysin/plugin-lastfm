using System.Threading.Channels;
using JellySin.Plugin.Lastfm.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Playback;

public sealed record PlaybackRuntimeStatus(bool LegacyPluginDetected, long DroppedSnapshots, long FailedWrites);

public sealed class PlaybackService(ISessionManager sessions, IPluginManager plugins, PlaybackTracker tracker,
    ScrobbleOutbox outbox, AccountService accounts, LibraryRuleService libraryRules, TimeProvider clock, ILogger<PlaybackService> logger) : BackgroundService
{
    // A loaded plugin is listed under the Id its assembly declares; the legacy Last.fm plugin declares de7fe7f0-...
    // while its catalog entry, and so a manifest Jellyfin has not rewritten yet, names 5e7fe7f0-....
    public static readonly IReadOnlyList<Guid> LegacyPluginIds =
        [new("de7fe7f0-b048-439e-a431-b1a7e99c930d"), new("5e7fe7f0-b048-429e-a431-b1a7e69c930d")];
    private readonly Channel<PlaybackSnapshot?> _snapshots = Channel.CreateBounded<PlaybackSnapshot?>(new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Channel<EligibleListen> _nowPlaying = Channel.CreateBounded<EligibleListen>(new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    // Users with something new to send. A full queue drops the request; the delivery task's next run sends it.
    private readonly Channel<Guid> _deliveryWork = Channel.CreateBounded<Guid>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private long _dropped;
    private long _failedWrites;
    private bool _legacyPresent;
    private int _retryCursor;

    public PlaybackRuntimeStatus GetStatus() => new(_legacyPresent, Interlocked.Read(ref _dropped), Interlocked.Read(ref _failedWrites));

    public int PendingPersistence(Guid userId) => tracker.PendingCount(userId);

    public ChannelReader<Guid> DeliveryWork => _deliveryWork.Reader;

    public void RequestDelivery(Guid userId) => _deliveryWork.Writer.TryWrite(userId);

    /// <summary>Asks the observation loop to retry listens it could not store; the retry runs on that loop, never beside it.</summary>
    public void RequestPersistenceRetry() => _snapshots.Writer.TryWrite(null);

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
        await libraryRules.InitializeAsync(cancellationToken).ConfigureAwait(false);
        _legacyPresent = plugins.Plugins.Any(plugin => LegacyPluginIds.Contains(plugin.Id) && (plugin.Instance is not null || plugin.IsEnabledAndSupported));
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
                    if (update.NowPlaying is not null && _nowPlaying.Writer.TryWrite(update.NowPlaying)) RequestDelivery(update.NowPlaying.UserId);
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
    }

    private async Task PersistAsync(EligibleListen listen, CancellationToken cancellationToken)
    {
        if (accounts.GetCaptureBinding(listen.UserId) is { } binding && binding.AccountGeneration != listen.AccountGeneration)
        { tracker.Acknowledge(listen.OccurrenceId); return; }
        await outbox.EnqueueAsync(listen, cancellationToken).ConfigureAwait(false);
        tracker.Acknowledge(listen.OccurrenceId);
        RequestDelivery(listen.UserId);
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
            if (_legacyPresent || Plugin.Instance?.ListeningEnabled == false) return;
            if (args.Item is not (Audio or MusicVideo) || args.Item is not IHasArtist performed || !libraryRules.IsMediaTypeAllowed(args.Item)) return;
            var item = args.Item;
            var artist = performed.Artists.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(item.Name) || artist.Length > 1024 || item.Name.Length > 1024) return;
            var sessionId = args.PlaySessionId ?? args.Session?.Id ?? args.DeviceId;
            if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 256) return;
            item.ProviderIds.TryGetValue("MusicBrainzTrack", out var mbid);
            var duration = (item.RunTimeTicks ?? 0) / (double)TimeSpan.TicksPerSecond;
            if (duration is <= 30 or > int.MaxValue) return;
            var track = new MusicTrack(item.Id, artist, item.Name, item.Album?.Length <= 4096 ? item.Album : null,
                Guid.TryParse(mbid, out var identifier) ? identifier.ToString() : null, duration,
                (item as IHasAlbumArtist)?.AlbumArtists.FirstOrDefault() is { Length: <= 1024 } albumArtist ? albumArtist : null);
            var timestamp = clock.GetTimestamp();
            var utc = clock.GetUtcNow();
            foreach (var user in args.Users.Take(32))
            {
                var binding = accounts.GetCaptureBinding(user.Id);
                if (binding is null) continue;
                if (!libraryRules.IsScrobblingAllowed(user.Id, item, args.DeviceId)) continue;
                var snapshot = new PlaybackSnapshot(sessionId, user.Id, track, signal, args.PlaybackPositionTicks ?? 0, args.IsPaused, signal == PlaybackSignal.Progress && args.IsAutomated, timestamp, utc, binding.AccountGeneration, binding.CaptureGeneration, Plugin.Instance?.ListeningGeneration ?? 0);
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
