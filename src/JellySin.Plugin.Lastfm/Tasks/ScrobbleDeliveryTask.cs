using JellySin.Plugin.Lastfm.Playback;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Tasks;

/// <summary>
/// Retries what the immediate delivery left: entries Last.fm deferred, entries waiting for a backoff, and listens that
/// could not be stored. Fresh plays do not wait for this task; <see cref="DeliveryService"/> sends them when stored.
/// </summary>
public sealed class ScrobbleDeliveryTask(PlaybackService playback, DeliveryService delivery, ILogger<ScrobbleDeliveryTask> logger) : IScheduledTask
{
    public string Name => "Retry Last.fm scrobble delivery";

    public string Key => "LastfmScrobbleDelivery";

    public string Description => "Retries pending scrobbles and now-playing updates that could not be sent to Last.fm yet.";

    public string Category => "Last.fm";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Plugin.Instance?.Configuration.Enabled == false) return;
        playback.RequestPersistenceRetry();
        try { await delivery.RunOnceAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning("Scrobble delivery retry failed with {ErrorType}.", ex.GetType().Name); }
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [new() { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(1).Ticks }];
}
