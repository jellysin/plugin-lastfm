using JellySin.Plugin.Lastfm.Api;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Transport;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Playback;

/// <summary>
/// Sends what playback stored as soon as it is stored, without a timer. Entries Last.fm deferred or that wait for a
/// backoff are sent by the "Retry Last.fm scrobble delivery" scheduled task, which calls <see cref="RunOnceAsync"/>.
/// One gate keeps the two from ever sending at the same time.
/// </summary>
public sealed class DeliveryService(PlaybackService playback, ScrobbleOutbox outbox, AccountService accounts, ILogger<DeliveryService> logger) : BackgroundService
{
    private const int BatchSize = 50;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _cursor;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await GuardedAsync(RunOnceAsync, stoppingToken).ConfigureAwait(false);
            var users = new HashSet<Guid>();
            await foreach (var first in playback.DeliveryWork.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                users.Clear();
                users.Add(first);
                for (var count = 0; count < 256 && playback.DeliveryWork.TryRead(out var other); count++) users.Add(other);
                await GuardedAsync(token => RunForUsersAsync(users, token), stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>Sends now-playing updates and every ready entry, visiting at most 64 accounts in turn.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Paused()) return;
            await DeliverNowPlayingAsync(cancellationToken).ConfigureAwait(false);
            await DeliverPendingScrobblesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }

    private async Task RunForUsersAsync(IReadOnlyCollection<Guid> users, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Paused()) return;
            await DeliverNowPlayingAsync(cancellationToken).ConfigureAwait(false);
            foreach (var userId in users) await FlushAsync(userId, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private bool Paused() => playback.GetStatus().LegacyPluginDetected || Plugin.Instance?.Configuration.Enabled == false;

    private async Task GuardedAsync(Func<CancellationToken, Task> run, CancellationToken stoppingToken)
    {
        try { await run(stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { logger.LogWarning("Scrobble delivery is unavailable ({ErrorType}).", exception.GetType().Name); }
    }

    private async Task DeliverNowPlayingAsync(CancellationToken cancellationToken)
    {
        for (var index = 0; index < 64 && playback.TryTakeNowPlaying(out var listen); index++)
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
            await FlushAsync(users[_cursor++ % users.Count], cancellationToken).ConfigureAwait(false);
        }
        _cursor %= users.Count;
    }

    private async Task FlushAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await outbox.FlushAsync(userId, cancellationToken).ConfigureAwait(false);
            if (outcome is null) return;
            LogFlush(userId, outcome);
            // A full accepted batch may leave more ready entries: ask for another pass instead of waiting for the task.
            if (outcome.ErrorCode is null && outcome.Submitted >= BatchSize) playback.RequestDelivery(userId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { logger.LogWarning("Scrobble delivery paused ({ErrorType}). Pending entries remain stored.", exception.GetType().Name); }
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
