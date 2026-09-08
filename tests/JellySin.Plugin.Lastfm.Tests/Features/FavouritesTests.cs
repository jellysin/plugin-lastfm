using JellySin.Plugin.Lastfm.Features;

namespace JellySin.Plugin.Lastfm.Tests.Features;

public sealed class FavouritesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FirstOptInMergesBothSidesAndDoesNotEchoWrites()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var local = f.AddLocal("Local", true);
        var remote = f.AddLocal("Remote");
        f.Loved.Add(remote.Track);
        var result = await f.Favourites.SetEnabledAsync(f.Core.UserId, true, Ct);
        Assert.Empty(result.Pending);
        Assert.Contains(f.Loved, t => t.Title == local.Track.Title);
        Assert.True(f.Library.Items[remote.Id].Favourite);
        Assert.Single(f.Library.FavouriteWrites);
        await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.Single(f.Library.FavouriteWrites);
        Assert.Single(f.Core.Client.Calls, c => c.Method == "track.love");
    }

    [Fact]
    public async Task LocalRemovalWaitsForReviewAndDoesNotGetReadded()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var track = f.AddLocal("Remove", true);
        await f.Favourites.SetEnabledAsync(f.Core.UserId, true, Ct);
        f.Library.Items[track.Id] = track with { Favourite = false };
        var review = await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.Equal("lastfm", Assert.Single(review.Pending).RemoveFrom);
        await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.False(f.Library.Items[track.Id].Favourite);
        Assert.Single(f.Loved);
        await f.Favourites.ReviewRemovalAsync(f.Core.UserId, review.Pending[0].Id, true, Ct);
        Assert.Empty(f.Loved);
    }

    [Fact]
    public async Task RemoteRemovalNeverMutatesJellyfinBeforeReview()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var track = f.AddLocal("Remote removal", true);
        await f.Favourites.SetEnabledAsync(f.Core.UserId, true, Ct);
        f.Loved.Clear();
        var review = await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.True(f.Library.Items[track.Id].Favourite);
        Assert.Equal("jellyfin", Assert.Single(review.Pending).RemoveFrom);
        await f.Favourites.ReviewRemovalAsync(f.Core.UserId, review.Pending[0].Id, true, Ct);
        Assert.False(f.Library.Items[track.Id].Favourite);
    }

    [Fact]
    public async Task StaleReviewCannotOverwriteAReaddedFavourite()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var track = f.AddLocal("Changed", true);
        await f.Favourites.SetEnabledAsync(f.Core.UserId, true, Ct);
        f.Library.Items[track.Id] = track with { Favourite = false };
        var review = await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        f.Library.Items[track.Id] = track;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Favourites.ReviewRemovalAsync(f.Core.UserId, review.Pending[0].Id, true, Ct));
        Assert.Single(f.Loved);
    }

    [Fact]
    public async Task IncompleteSnapshotDoesNotCreateRemovalsOrAdditions()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var track = f.AddLocal("Incomplete", true);
        await f.Favourites.SetEnabledAsync(f.Core.UserId, true, Ct);
        f.Loved.Clear();
        f.IncompleteLoved = true;
        var review = await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.Empty(review.Pending);
        Assert.Contains("incomplete", review.Status!, StringComparison.Ordinal);
        Assert.True(f.Library.Items[track.Id].Favourite);
    }

    [Fact]
    public async Task AmbiguousMatchingDoesNotInventRemoteRemoval()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var track = f.AddLocal("Duplicate", true);
        await f.Favourites.SetEnabledAsync(f.Core.UserId, true, Ct);
        f.AddLocal("Duplicate");
        var review = await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.Empty(review.Pending);
        Assert.True(f.Library.Items[track.Id].Favourite);
    }

    [Fact]
    public async Task ReviewIdIsBoundToItsUserAndDisablingPreservesFavourites()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var track = f.AddLocal("Scoped", true);
        await f.Favourites.SetEnabledAsync(f.Core.UserId, true, Ct);
        f.Loved.Clear();
        var review = await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Favourites.ReviewRemovalAsync(Guid.NewGuid(), review.Pending[0].Id, true, Ct));
        await f.Favourites.SetEnabledAsync(f.Core.UserId, false, Ct);
        Assert.True(f.Library.Items[track.Id].Favourite);
        Assert.Empty(f.Library.FavouriteWrites);
    }
}
