using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Features;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Tasks;

public sealed class FavouriteSyncTask(AccountService accounts, FavouriteRuns runs, ILogger<FavouriteSyncTask> logger) : AccountTask(accounts, logger)
{
    public override string Name => "Reconcile Last.fm favourites";

    public override string Key => "LastfmFavouriteSync";

    public override string Description => "Reconciles Jellyfin favourites with Last.fm loved tracks for connected accounts.";

    protected override string Operation => "Favourite reconciliation";

    protected override Task RunAsync(Guid userId, CancellationToken cancellationToken) => runs.RunToEndAsync(userId, cancellationToken);
}
