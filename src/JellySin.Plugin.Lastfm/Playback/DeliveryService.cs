using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Transport;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Playback;

public sealed class DeliveryService(PlaybackService playback, ScrobbleOutbox outbox, AccountService accounts, TimeProvider clock, ILogger<DeliveryService> logger) : BackgroundService
{
    private int _cursor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await RunOnceAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
                catch (Exception exception) { logger.LogWarning("Scrobble delivery is unavailable ({ErrorType}).", exception.GetType().Name); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (playback.GetStatus().LegacyPluginDetected || Plugin.Instance?.Configuration.Enabled == false) return;
        for (var index = 0; index < 8 && playback.TryTakeNowPlaying(out var listen); index++)
        {
            try { await outbox.NowPlayingAsync(listen!, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (LastfmException) { /* The protocol forbids retrying failed now-playing requests. */ }
            catch (Exception exception) { logger.LogWarning("Now-playing update failed ({ErrorType}).", exception.GetType().Name); }
        }
        var users = await accounts.GetAccountsAsync(cancellationToken).ConfigureAwait(false);
        if (users.Count == 0) return;
        for (var index = 0; index < Math.Min(64, users.Count); index++)
        {
            var userId = users[_cursor++ % users.Count];
            try { await outbox.FlushAsync(userId, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { logger.LogWarning("Scrobble delivery paused ({ErrorType}). Pending entries remain stored.", exception.GetType().Name); }
        }
        _cursor %= users.Count;
    }
}
