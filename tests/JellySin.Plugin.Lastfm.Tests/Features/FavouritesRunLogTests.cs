using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Tests.Core;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Tests.Features;

/// <summary>A finished favourite run writes one information line with counts; a failed or cancelled run writes none.</summary>
public sealed class FavouritesRunLogTests
{
    private const string Artist = "Hidden Artist";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FinishedRunWritesOneLineWithTheCountsAndNoNames()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        f.AddLocal("Only on the server", favourite: true, artist: Artist);
        var remote = f.AddLocal("Only on Last.fm", artist: Artist);
        f.Loved.Add(remote.Track);

        await f.EnableAndSyncAsync(Ct);

        var line = Assert.Single(f.FavouriteLog.Lines);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Equal(
            $"Favourite synchronization for user {Logged.User(f.Core.UserId)} finished all tracks: 1 loved on Last.fm, 1 set as server favourites, 0 removals awaiting review, 0 ambiguous tracks left unchanged.",
            line.Text);
        Assert.DoesNotContain("TestListener", line.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Artist, line.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(f.Core.UserId.ToString(), line.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunThatLeavesARemovalForReviewCountsItAndUnlovesNothing()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var local = f.AddLocal("Loved twice", favourite: true);
        f.Loved.Add(local.Track);
        await f.EnableAndSyncAsync(Ct);
        f.Library.SetFavourite(f.Core.UserId, local.Id, false, Ct);

        await f.Favourites.SyncAsync(f.Core.UserId, Ct);

        Assert.Equal(2, f.FavouriteLog.Lines.Count);
        Assert.EndsWith("finished all tracks: 0 loved on Last.fm, 0 set as server favourites, 1 removals awaiting review, 0 ambiguous tracks left unchanged.",
            f.FavouriteLog.Lines[1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(f.Core.Client.Calls, call => call.Method == "track.unlove");
    }

    [Fact]
    public async Task RunThatStopsAtItsWindowSaysMoreRemain()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        for (var index = 0; index < 201; index++) f.AddLocal("Bounded " + index, true);

        await f.EnableAndSyncAsync(Ct);

        var line = Assert.Single(f.FavouriteLog.Lines);
        Assert.EndsWith("finished 200 tracks, more remain: 200 loved on Last.fm, 0 set as server favourites, 0 removals awaiting review, 0 ambiguous tracks left unchanged.",
            line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedRunWritesNoLine()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        f.Loved.Add(f.AddLocal("Fails").Track);
        await f.Favourites.SetEnabledAsync(f.Core.UserId, true, Ct);
        f.Library.OnMatch = _ => throw new IOException("planted");

        await Assert.ThrowsAsync<IOException>(() => f.Favourites.SyncAsync(f.Core.UserId, Ct));

        Assert.Empty(f.FavouriteLog.Lines);
    }

    [Fact]
    public async Task CancelledRunWritesNoLine()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        f.Loved.Add(f.AddLocal("Cancelled").Track);
        await f.Favourites.SetEnabledAsync(f.Core.UserId, true, Ct);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        f.Library.OnMatch = _ => cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Favourites.SyncAsync(f.Core.UserId, cancel.Token));

        Assert.Empty(f.FavouriteLog.Lines);
    }

    [Fact]
    public async Task DisabledRunWritesNoLine()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        f.AddLocal("Disabled", favourite: true);

        await f.Favourites.SyncAsync(f.Core.UserId, Ct);

        Assert.Empty(f.FavouriteLog.Lines);
    }
}
