using JellySin.Plugin.Lastfm.Api;
using JellySin.Plugin.Lastfm.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Tasks;

/// <summary>A scheduled task that runs one operation for every connected account, at most 8192, one run at a time.</summary>
public abstract class AccountTask(AccountService accounts, ILogger logger) : IScheduledTask, IDisposable
{
    private const int MaxAccounts = 8192;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public abstract string Name { get; }

    public abstract string Key { get; }

    public abstract string Description { get; }

    public string Category => "Last.fm";

    protected abstract string Operation { get; }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Plugin.Instance?.Configuration.Enabled == false) return;
        // Jellyfin starts one run of a task at a time; this keeps a second caller from overlapping it as well.
        if (!_gate.Wait(0)) return;
        try
        {
            var ids = await accounts.GetAccountsAsync(cancellationToken).ConfigureAwait(false);
            var total = Math.Min(ids.Count, MaxAccounts);
            for (var index = 0; index < total; index++)
            {
                await RunForAccountAsync(ids[index], cancellationToken).ConfigureAwait(false);
                progress.Report((index + 1) * 100.0 / total);
            }
        }
        finally { _gate.Release(); }
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [new() { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromMinutes(5).Ticks }];

    // No subclass owns anything to release, so the base disposes its gate directly.
    public void Dispose()
    {
        _gate.Dispose();
        GC.SuppressFinalize(this);
    }

    protected abstract Task RunAsync(Guid userId, CancellationToken cancellationToken);

    private async Task RunForAccountAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var account = await accounts.GetAsync(userId, cancellationToken).ConfigureAwait(false);
            if (account is not null && !account.NeedsReconnect) await RunAsync(userId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning("{Operation} for user {User} failed with {ErrorType}.", Operation, LogName.Of(userId), ex.GetType().Name);
        }
    }
}
