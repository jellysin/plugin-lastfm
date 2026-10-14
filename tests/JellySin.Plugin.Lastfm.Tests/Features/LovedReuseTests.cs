using Jellyfin.Database.Implementations.Entities;
using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Tests.Core;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Users;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Features;

public sealed class LovedReuseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SecondRunOnUnchangedFavouritesMakesNoLibraryMatchCall()
    {
        using var f = await SyncedAsync(3);
        var matches = f.Library.MatchCalls;
        var review = await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.Equal(matches, f.Library.MatchCalls);
        Assert.Equal(1, LovedPageCalls(f));
        Assert.Empty(review.Pending);
    }

    [Fact]
    public async Task ATrackAddedToTheLibraryIsMatchedByTheNextRun()
    {
        using var f = await SyncedAsync(2, new MusicTrack("Artist", "Arrives later"));
        var later = f.AddLocal("Arrives later");
        await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.Equal(1, LovedPageCalls(f));
        Assert.True(f.Library.Items[later.Id].Favourite);
    }

    [Fact]
    public async Task ALoveAddedOnLastfmIsSeenByTheNextRun()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var elsewhere = f.AddLocal("Loved elsewhere");
        await SyncAsync(f, 2);
        f.Loved.Insert(0, elsewhere.Track with { PlayedAt = f.Core.Clock.GetUtcNow() });
        await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.True(f.Library.Items[elsewhere.Id].Favourite);
    }

    [Fact]
    public async Task ASwapWithTheSameTotalIsSeenByTheNextRun()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var swapped = f.AddLocal("Swapped in");
        await SyncAsync(f, 3);
        var removed = f.Loved[^1];
        f.Loved.RemoveAt(f.Loved.Count - 1);
        f.Loved.Insert(0, swapped.Track with { PlayedAt = f.Core.Clock.GetUtcNow() });
        var review = await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.True(f.Library.Items[swapped.Id].Favourite);
        var pending = Assert.Single(review.Pending);
        Assert.Equal(("jellyfin", removed.Title), (pending.RemoveFrom, pending.Track.Title));
    }

    [Fact]
    public async Task ThisPluginsOwnLoveIsReadByTheNextRunAndKeptAfter()
    {
        using var f = await SyncedAsync(2);
        f.AddLocal("Loved here", true);
        await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.Contains(f.Loved, track => track.Title == "Loved here");
        f.Core.Client.Calls.Clear();
        var review = await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.Empty(review.Pending);
        Assert.Equal(2, LovedPageCalls(f));
        f.Core.Client.Calls.Clear();
        await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.Equal(1, LovedPageCalls(f));
    }

    [Fact]
    public async Task AnAccessChangeMatchesAgain()
    {
        using var f = await SyncedAsync(2);
        var matches = f.Library.MatchCalls;
        f.Library.Policy = "music-library-only";
        await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.Equal(matches + 2, f.Library.MatchCalls);
    }

    [Fact]
    public async Task ARemovalReviewReusesTheRunAndRevalidatesOnlyItsTrack()
    {
        using var f = await SyncedAsync(3);
        f.Loved.RemoveAt(2);
        var pending = Assert.Single((await f.Favourites.SyncAsync(f.Core.UserId, Ct)).Pending);
        f.Core.Client.Calls.Clear();
        var matches = f.Library.MatchCalls;
        await f.Favourites.ReviewRemovalAsync(f.Core.UserId, pending.Id, true, Ct);
        Assert.Equal(matches + 1, f.Library.MatchCalls);
        Assert.Equal(1, LovedPageCalls(f));
        Assert.False(f.Library.Items[pending.ItemId].Favourite);
    }

    [Fact]
    public async Task ARemovalReviewSeesALoveRestoredOnLastfm()
    {
        using var f = await SyncedAsync(3);
        var removed = f.Loved[2];
        f.Loved.RemoveAt(2);
        var pending = Assert.Single((await f.Favourites.SyncAsync(f.Core.UserId, Ct)).Pending);
        f.Loved.Insert(0, removed with { PlayedAt = f.Core.Clock.GetUtcNow() });
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Favourites.ReviewRemovalAsync(f.Core.UserId, pending.Id, true, Ct));
        Assert.True(f.Library.Items[pending.ItemId].Favourite);
    }

    [Fact]
    public void KeptCollectionIsForTheSameAccountAndAtMostADay()
    {
        var (reuse, clock, _) = Reuse();
        var user = Guid.NewGuid();
        var page = Page(3);
        reuse.Keep(user, "listener", page, page);
        Assert.Same(page, reuse.Reuse(user, "listener", page));
        Assert.Null(reuse.Reuse(user, "another-listener", page));
        clock.Advance(LovedReuse.MaxAge.TotalSeconds - 1);
        Assert.Same(page, reuse.Reuse(user, "listener", page));
        clock.Advance(1);
        Assert.Null(reuse.Reuse(user, "listener", page));
    }

    [Fact]
    public void AChangeBeyondPageOneIsSeenInTheTotalOrPageCount()
    {
        var (reuse, _, _) = Reuse();
        var user = Guid.NewGuid();
        var first = Page(200) with { TotalPages = 2, Complete = false, TotalTracks = 250 };
        var collection = Page(250) with { TotalPages = 2 };
        reuse.Keep(user, "listener", first, collection);
        Assert.Same(collection, reuse.Reuse(user, "listener", first));
        Assert.Null(reuse.Reuse(user, "listener", first with { TotalTracks = 249 }));
        Assert.Null(reuse.Reuse(user, "listener", first with { TotalPages = 3 }));
    }

    [Fact]
    public void OnlyAWholeConsistentCollectionWithinTheTrackBoundIsKept()
    {
        var (reuse, _, _) = Reuse();
        var user = Guid.NewGuid();
        var largest = Page(LovedReuse.MaxTracks);
        reuse.Keep(user, "listener", largest, largest);
        Assert.Same(largest, reuse.Reuse(user, "listener", largest));
        var oversized = Page(LovedReuse.MaxTracks + 1);
        reuse.Keep(user, "listener", oversized, oversized);
        Assert.Null(reuse.Reuse(user, "listener", oversized));
        var incomplete = Page(3) with { Complete = false };
        reuse.Keep(user, "listener", incomplete, incomplete);
        Assert.Null(reuse.Reuse(user, "listener", incomplete));
        var first = Page(3);
        reuse.Keep(user, "listener", first, Page(3, offset: 1));
        Assert.Null(reuse.Reuse(user, "listener", first));
    }

    [Fact]
    public void KeptCollectionsAreBoundedToTheNewestUsers()
    {
        var (reuse, clock, _) = Reuse();
        var users = Enumerable.Range(0, LovedReuse.MaxUsers + 1).Select(_ => Guid.NewGuid()).ToArray();
        var page = Page(1);
        foreach (var user in users)
        {
            reuse.Keep(user, "listener", page, page);
            clock.Advance(1);
        }
        Assert.Null(reuse.Reuse(users[0], "listener", page));
        Assert.All(users.Skip(1), user => Assert.Same(page, reuse.Reuse(user, "listener", page)));
    }

    [Fact]
    public void MatchesAreBoundedInTracksAndAge()
    {
        var (reuse, clock, library) = Reuse();
        var user = Guid.NewGuid();
        var tracks = Page(LovedReuse.MaxTracks + 1).Tracks;
        var calls = 0;
        Guid? Match(MusicTrack track) { calls++; return null; }
        reuse.Match(user, tracks, Match);
        Assert.Equal(LovedReuse.MaxTracks + 1, calls);
        reuse.Match(user, tracks, Match);
        Assert.Equal(LovedReuse.MaxTracks + 2, calls);
        clock.Advance(LovedReuse.MaxAge.TotalSeconds);
        reuse.Match(user, tracks, Match);
        Assert.Equal(2 * LovedReuse.MaxTracks + 3, calls);
        library.Items[Guid.NewGuid()] = new(Guid.NewGuid(), new("Artist", "New"), false, 0, null);
        reuse.Match(user, tracks.Take(1).ToArray(), Match);
        Assert.Equal(2 * LovedReuse.MaxTracks + 4, calls);
    }

    [Fact]
    public void MatchSetsAreBoundedToTheNewestUsers()
    {
        var (reuse, clock, _) = Reuse();
        var tracks = Page(1).Tracks;
        var calls = 0;
        Guid? Match(MusicTrack track) { calls++; return null; }
        var users = Enumerable.Range(0, LovedReuse.MaxUsers + 1).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var user in users)
        {
            reuse.Match(user, tracks, Match);
            clock.Advance(1);
        }
        reuse.Match(users[^1], tracks, Match);
        Assert.Equal(users.Length, calls);
        reuse.Match(users[0], tracks, Match);
        Assert.Equal(users.Length + 1, calls);
    }

    [Fact]
    public void EveryLibraryItemEventMovesTheGenerationUntilDisposed()
    {
        var library = new Mock<ILibraryManager>();
        var generation = new LibraryGeneration(library.Object, Mock.Of<IUserManager>());
        var args = new ItemChangeEventArgs { Item = new Audio() };
        library.Raise(value => value.ItemAdded += null, library.Object, args);
        library.Raise(value => value.ItemUpdated += null, library.Object, args);
        library.Raise(value => value.ItemRemoved += null, library.Object, args);
        Assert.Equal(3, generation.Current);
        generation.Dispose();
        library.Raise(value => value.ItemAdded += null, library.Object, args);
        Assert.Equal(3, generation.Current);
    }

    [Fact]
    public void AccessFollowsThePolicyAndIsEmptyForAnUnknownUser()
    {
        var user = new User("Listener", "provider", "reset");
        var policy = new UserPolicy { EnableAllFolders = true };
        var users = new Mock<IUserManager>();
        users.Setup(value => value.GetUserById(user.Id)).Returns(user);
        users.Setup(value => value.GetUserDto(user, It.IsAny<string?>())).Returns(() => new UserDto { Policy = policy });
        using var generation = new LibraryGeneration(Mock.Of<ILibraryManager>(), users.Object);
        var before = generation.Access(user.Id);
        Assert.Equal(before, generation.Access(user.Id));
        policy = new UserPolicy { EnableAllFolders = false };
        Assert.NotEqual(before, generation.Access(user.Id));
        Assert.Equal(string.Empty, generation.Access(Guid.NewGuid()));
    }

    private static async Task<FeatureFixture> SyncedAsync(int count, params MusicTrack[] lovedElsewhere)
    {
        var f = new FeatureFixture();
        await f.InitializeAsync();
        f.Loved.AddRange(lovedElsewhere);
        await SyncAsync(f, count);
        return f;
    }

    /// <summary>Adds tracks that are favourites on both sides, runs once and clears the call record.</summary>
    private static async Task SyncAsync(FeatureFixture f, int count)
    {
        for (var index = 0; index < count; index++) f.Loved.Add(f.AddLocal("Song " + index, true).Track);
        await f.EnableAndSyncAsync(Ct);
        f.Core.Client.Calls.Clear();
    }

    private static int LovedPageCalls(FeatureFixture f) => f.Core.Client.Calls.Count(call => call.Method == "user.getLovedTracks");

    private static (LovedReuse Reuse, TestClock Clock, FakeMusicLibrary Library) Reuse()
    {
        var clock = new TestClock();
        var library = new FakeMusicLibrary();
        return (new(library, clock), clock, library);
    }

    private static MusicPage Page(int count, int offset = 0) =>
        new(Enumerable.Range(offset, count).Select(index => new MusicTrack("Artist", "Song " + index)).ToArray(), 1, 1, true, TotalTracks: count);
}
