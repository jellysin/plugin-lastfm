using JellySin.Plugin.Lastfm.Features;

namespace JellySin.Plugin.Lastfm.Tests.Features;

public sealed class FavouritesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task InterruptedRemoteAdditionCannotReaddAConcurrentLocalRemoval()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var local = f.AddLocal("Crash after love", true);
        f.Core.Client.Handler = (method, arguments, session) =>
        {
            var response = f.Respond(method, arguments, session);
            if (method != "track.love") return response;
            response.Dispose();
            f.Library.SetFavourite(f.Core.UserId, local.Id, false, Ct);
            throw new IOException("Simulated process loss after remote acceptance.");
        };
        await Assert.ThrowsAsync<IOException>(() => f.Favourites.SetEnabledAsync(f.Core.UserId, true, Ct));
        Assert.Single(f.Loved);
        Assert.NotNull(await f.Core.Store.ReadAsync<List<FavouriteAddition>>(f.Core.UserId, "feature-favourite-additions", Ct));
        f.Core.Client.Handler = f.Respond;
        var recovered = await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.False(f.Library.Items[local.Id].Favourite);
        Assert.Equal("lastfm", Assert.Single(recovered.Pending).RemoveFrom);
        Assert.Null(await f.Core.Store.ReadAsync<List<FavouriteAddition>>(f.Core.UserId, "feature-favourite-additions", Ct));
    }

    [Fact]
    public async Task InterruptedAdditionBeforeAcceptanceRetriesAdditively()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        f.AddLocal("Unaccepted love", true);
        f.Core.Client.Handler = (method, arguments, session) => method == "track.love"
            ? throw new IOException("Connection failed before acceptance.") : f.Respond(method, arguments, session);
        await Assert.ThrowsAsync<IOException>(() => f.Favourites.SetEnabledAsync(f.Core.UserId, true, Ct));
        f.Core.Client.Handler = f.Respond;
        Assert.Empty((await f.Favourites.SyncAsync(f.Core.UserId, Ct)).Pending);
        Assert.Single(f.Loved);
    }

    [Fact]
    public async Task LargeAdditiveMergeCheckpointsAndResumesAtTwoHundredTracks()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        for (var index = 0; index < 201; index++) f.AddLocal("Bounded " + index, true);
        var first = await f.Favourites.SetEnabledAsync(f.Core.UserId, true, Ct);
        Assert.Equal(200, f.Loved.Count);
        Assert.Contains("continue", first.Status!, StringComparison.Ordinal);
        var next = await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.Equal(201, f.Loved.Count);
        Assert.Equal(201, f.Core.Client.Calls.Count(call => call.Method == "track.love"));
        Assert.NotNull(next.LastSync);
    }

    [Fact]
    public async Task RejectedAmbiguousNamesCannotInventAnAddition()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var first = f.AddLocal("Collision");
        f.AddLocal("Collision");
        f.Loved.Add(first.Track);
        await f.Core.Store.WriteAsync(f.Core.UserId, "feature-favourites",
            new FavouriteState(true, new() { [first.Id] = new(first.Track, false, false) }, []), Ct);
        var result = await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.Empty(f.Library.FavouriteWrites);
        Assert.Empty(result.Pending);
        Assert.Contains("unresolved", result.Status!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LocalRemoveReaddCannotReuseAnOldRemovalReview()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var local = f.AddLocal("Local ABA", true);
        await f.Favourites.SetEnabledAsync(f.Core.UserId, true, Ct);
        f.Loved.Clear();
        var original = Assert.Single((await f.Favourites.SyncAsync(f.Core.UserId, Ct)).Pending);
        f.Library.SetFavourite(f.Core.UserId, local.Id, false, Ct);
        f.Library.SetFavourite(f.Core.UserId, local.Id, true, Ct);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Favourites.ReviewRemovalAsync(f.Core.UserId, original.Id, true, Ct));
        Assert.True(f.Library.Items[local.Id].Favourite);
        var refreshed = Assert.Single((await f.Favourites.SyncAsync(f.Core.UserId, Ct)).Pending);
        Assert.NotEqual(original.Id, refreshed.Id);
    }

    [Fact]
    public async Task RemoteReloveDateInvalidatesAnOldRemovalReview()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var local = f.AddLocal("Remote ABA", true);
        await f.Favourites.SetEnabledAsync(f.Core.UserId, true, Ct);
        f.Library.SetFavourite(f.Core.UserId, local.Id, false, Ct);
        var original = Assert.Single((await f.Favourites.SyncAsync(f.Core.UserId, Ct)).Pending);
        f.Loved[0] = f.Loved[0] with { PlayedAt = f.Core.Clock.GetUtcNow().AddSeconds(1) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Favourites.ReviewRemovalAsync(f.Core.UserId, original.Id, true, Ct));
        Assert.DoesNotContain(f.Core.Client.Calls, c => c.Method == "track.unlove");
    }

    [Fact]
    public async Task MissingRemoteEditTimestampCannotAuthorizeDestructiveRemoteWrite()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var local = f.AddLocal("Unknown edit", true);
        f.Loved.Add(local.Track);
        await f.Favourites.SetEnabledAsync(f.Core.UserId, true, Ct);
        f.Library.SetFavourite(f.Core.UserId, local.Id, false, Ct);
        var review = Assert.Single((await f.Favourites.SyncAsync(f.Core.UserId, Ct)).Pending);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Favourites.ReviewRemovalAsync(f.Core.UserId, review.Id, true, Ct));
        Assert.Single(f.Loved);
    }

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
