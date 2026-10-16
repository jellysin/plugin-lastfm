using JellySin.Plugin.Lastfm.Api;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Features;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Tasks;

/// <summary>
/// After a library scan, starts the favourite run of every connected account, so loved tracks the scan added are
/// matched now instead of at the next timer tick. A run matches only the tracks still pending and those the scan touched.
/// The scan does not wait for the runs.
/// </summary>
public sealed class LibraryScanRematchTask(AccountService accounts, FavouriteRuns runs, ILogger<LibraryScanRematchTask> logger) : ILibraryPostScanTask
{
    private const int MaxAccounts = 8192;

    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Plugin.Instance?.Configuration.Enabled == false) return;
        var ids = await accounts.GetAccountsAsync(cancellationToken).ConfigureAwait(false);
        var total = Math.Min(ids.Count, MaxAccounts);
        for (var index = 0; index < total; index++)
        {
            var account = await accounts.GetAsync(ids[index], cancellationToken).ConfigureAwait(false);
            if (account is not null && !account.NeedsReconnect && !runs.TryStart(ids[index]))
            {
                logger.LogInformation("Favourite runs after the library scan stopped at user {User}: too many runs wait.", LogName.Of(ids[index]));
                break;
            }
            progress.Report((index + 1) * 100.0 / total);
        }
    }
}
