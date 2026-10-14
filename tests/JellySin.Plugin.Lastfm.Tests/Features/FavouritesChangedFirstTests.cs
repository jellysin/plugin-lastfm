using JellySin.Plugin.Lastfm.Features;

namespace JellySin.Plugin.Lastfm.Tests.Features;

/// <summary>A run reconciles 200 track ids in id order; after the first full pass it reconciles changed ids first.</summary>
public sealed class FavouritesChangedFirstTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ALocalFavouriteOutsideTheWindowIsLovedByTheNextRun()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var items = Seed(f, 402, loved: false);
        var target = items[^1];
        f.Library.Items[target.Id] = target with { Favourite = false };
        await FullPassAsync(f);
        Assert.DoesNotContain(f.Loved, track => track.Title == target.Track.Title);
        f.Library.Items[target.Id] = target;
        var next = await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.Contains(f.Loved, track => track.Title == target.Track.Title);
        Assert.Contains("continue", next.Status!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALastfmRemovalOutsideTheWindowIsProposedByTheNextRun()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var target = Seed(f, 401, loved: true)[^1];
        await FullPassAsync(f);
        f.Loved.RemoveAll(track => track.Title == target.Track.Title);
        var next = await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        var proposal = Assert.Single(next.Pending);
        Assert.Equal((target.Id, "jellyfin"), (proposal.ItemId, proposal.RemoveFrom));
        Assert.True(f.Library.Items[target.Id].Favourite);
    }

    [Fact]
    public async Task ALoveThisPluginSentIsNoChangeUntilLastfmListsIt()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var target = Seed(f, 300, loved: false)[^1];
        await FullPassAsync(f);
        Assert.Equal(300, f.Loved.Count);
        // Last.fm does not list the love yet; the run must not read that as a removal on Last.fm.
        f.Loved.RemoveAll(track => track.Title == target.Track.Title);
        var next = await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.DoesNotContain("paused", next.Status!, StringComparison.Ordinal);
        Assert.Empty(next.Pending);
        Assert.True(f.Library.Items[target.Id].Favourite);
    }

    [Fact]
    public async Task ARunReconcilesAtMostTwoHundredChangedTracksBesideItsWindow()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var items = Seed(f, 601, loved: true);
        await FullPassAsync(f);
        foreach (var item in items.Skip(400)) f.Library.Items[item.Id] = item with { Favourite = false };
        var next = await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.Equal(200, next.Pending.Count);
        Assert.All(next.Pending, proposal => Assert.Equal("lastfm", proposal.RemoveFrom));
        Assert.Contains("continue", next.Status!, StringComparison.Ordinal);
    }

    /// <summary>Local favourites in id order; when loved, Last.fm lists each with its own loved date.</summary>
    private static List<LocalMusic> Seed(FeatureFixture f, int count, bool loved)
    {
        var items = Enumerable.Range(0, count).Select(index => f.AddLocal($"Pass {index:000}", true)).OrderBy(item => item.Id).ToList();
        if (loved) f.Loved.AddRange(items.Select((item, index) => item.Track with { PlayedAt = f.Core.Clock.GetUtcNow().AddMinutes(-index) }));
        return items;
    }

    private static async Task FullPassAsync(FeatureFixture f)
    {
        var review = await f.EnableAndSyncAsync(Ct);
        for (var run = 0; run < 10 && review.LastSync is null; run++) review = await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.True(review.LastSync is not null, review.Status);
        Assert.Empty(review.Pending);
    }
}
