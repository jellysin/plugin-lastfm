using JellySin.Plugin.Lastfm.Api;
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
        await DeliverNowPlayingAsync(cancellationToken).ConfigureAwait(false);
        await DeliverPendingScrobblesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task DeliverNowPlayingAsync(CancellationToken cancellationToken)
    {
        for (var index = 0; index < 8 && playback.TryTakeNowPlaying(out var listen); index++)
        {
            try
            {
                var outcome = await outbox.NowPlayingAsync(listen!, cancellationToken, () => playback.IsCurrentNowPlaying(listen!)).ConfigureAwait(false);
                LogNowPlaying(listen!.UserId, outcome);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (LastfmException exception)
            {
                if (exception.Code != 8 || playback.IsCurrentNowPlaying(listen!))
                    logger.LogInformation("Playing-now update for user {User}: Last.fm refused it with error {Code}. It is not repeated.", LogName.Of(listen!.UserId), exception.Code);
            }
            catch (Exception exception) { logger.LogWarning("Now-playing update failed ({ErrorType}).", exception.GetType().Name); }
        }
    }

    private async Task DeliverPendingScrobblesAsync(CancellationToken cancellationToken)
    {
        var users = await accounts.GetAccountsAsync(cancellationToken).ConfigureAwait(false);
        if (users.Count == 0) return;
        for (var index = 0; index < Math.Min(64, users.Count); index++)
        {
            var userId = users[_cursor++ % users.Count];
            try
            {
                var outcome = await outbox.FlushAsync(userId, cancellationToken).ConfigureAwait(false);
                if (outcome is not null) LogFlush(userId, outcome);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { logger.LogWarning("Scrobble delivery paused ({ErrorType}). Pending entries remain stored.", exception.GetType().Name); }
        }
        _cursor %= users.Count;
    }

    private void LogNowPlaying(Guid userId, NowPlayingOutcome outcome)
    {
        switch (outcome.Answer)
        {
            case NowPlayingAnswer.Accepted:
                logger.LogInformation("Playing-now update for user {User}: Last.fm accepted it.", LogName.Of(userId));
                break;
            case NowPlayingAnswer.Ignored:
                logger.LogInformation("Playing-now update for user {User}: Last.fm ignored it (ignore code {Code}).", LogName.Of(userId), outcome.IgnoreCode);
                break;
            case NowPlayingAnswer.NoIgnoreCode:
                logger.LogInformation("Playing-now update for user {User}: Last.fm answered without an error.", LogName.Of(userId));
                break;
            case NowPlayingAnswer.NotSent:
                break;
        }
    }

    private void LogFlush(Guid userId, FlushOutcome outcome)
    {
        if (outcome.ErrorCode is { } errorCode)
        {
            logger.LogWarning("Submitting plays for user {User} failed with Last.fm error {Code}. All {Submitted} remain stored.", LogName.Of(userId), errorCode, outcome.Submitted);
            return;
        }
        var ignored = outcome.Submitted - outcome.Accepted;
        var codes = outcome.IgnoredCodes.Count == 0 ? "none" : string.Join(", ", outcome.IgnoredCodes);
        logger.LogInformation("Plays submitted for user {User}: {Submitted} sent, Last.fm accepted {Accepted} and ignored {Ignored} (ignore codes: {Codes}).",
            LogName.Of(userId), outcome.Submitted, outcome.Accepted, ignored, codes);
    }
}
