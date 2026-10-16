using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Features;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Tasks;

public sealed class PlaylistRefreshTask(AccountService accounts, PlaylistService playlists, ILogger<PlaylistRefreshTask> logger) : AccountTask(accounts, logger)
{
    public override string Name => "Refresh Last.fm playlists";

    public override string Key => "LastfmPlaylistRefresh";

    public override string Description => "Refreshes dynamic Last.fm playlists that are due for update.";

    protected override string Operation => "Playlist refresh";

    protected override Task RunAsync(Guid userId, CancellationToken cancellationToken) => playlists.RefreshDueAsync(userId, cancellationToken);
}
