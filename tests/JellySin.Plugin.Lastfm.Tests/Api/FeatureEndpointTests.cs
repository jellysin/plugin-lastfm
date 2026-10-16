using System.Text.Json;
using JellySin.Plugin.Lastfm.Api;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Playback;
using JellySin.Plugin.Lastfm.Tests.Features;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Api;

public sealed class FeatureEndpointTests
{
    [Fact]
    public async Task PendingPlaylistCancellationUsesCallerAndRejectsAnObsoleteOperationId()
    {
        using var fixture = new FeatureFixture();
        await fixture.InitializeAsync();
        var discovery = new DiscoveryService(fixture.Api, fixture.Library, Mock.Of<IDiscoveryLibrary>(), fixture.Core.Accounts);
        var manager = new Mock<IPlaylistManager>(MockBehavior.Strict);
        var service = new PlaylistService(fixture.Api, discovery, fixture.Library, Mock.Of<ILibraryManager>(), manager.Object,
            fixture.Core.Store, fixture.Locks, fixture.Core.Clock, fixture.Core.Accounts);
        var recipe = new PlaylistRecipe(Guid.NewGuid(), "Recover my music", PlaylistSource.Top, DailyRefresh: true, PlaylistId: Guid.NewGuid());
        var operation = new PlaylistOperation(recipe, [Guid.NewGuid()], recipe.PlaylistId, Guid.NewGuid(), "A track is unavailable.");
        await fixture.Core.Store.WriteAsync(fixture.Core.UserId, "feature-playlist-operation", operation, TestContext.Current.CancellationToken);
        var controller = Attach(new PlaylistsController(service), fixture.Core.UserId);
        controller.Request.QueryString = new QueryString("?userId=" + Guid.NewGuid());
        var pending = JsonSerializer.SerializeToElement(await controller.Pending(TestContext.Current.CancellationToken));
        Assert.Equal(operation.OperationId, pending.GetProperty("OperationId").GetGuid());
        Assert.Equal(1, pending.GetProperty("ItemCount").GetInt32());
        Assert.False(pending.TryGetProperty("Items", out _));
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.CancelPending(Guid.NewGuid(), TestContext.Current.CancellationToken));
        Assert.NotNull(await service.GetPendingOperationAsync(fixture.Core.UserId, TestContext.Current.CancellationToken));
        Assert.IsType<NoContentResult>(await controller.CancelPending(operation.OperationId, TestContext.Current.CancellationToken));
        Assert.Null(await controller.Pending(TestContext.Current.CancellationToken));
        manager.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FavouriteEndpointsSyncAndReviewOnlyAuthenticatedUsersState()
    {
        using var fixture = new FeatureFixture();
        await fixture.InitializeAsync();
        var local = fixture.AddLocal("Favourite track", true);
        var controller = Attach(new FavouritesController(fixture.Favourites, fixture.Runs), fixture.Core.UserId);
        Assert.False((await controller.Get(TestContext.Current.CancellationToken)).Enabled);
        Assert.True((await controller.Enable(new ToggleRequest(true), TestContext.Current.CancellationToken)).Enabled);
        Assert.Single(fixture.Loved);
        fixture.Library.Items[local.Id] = local with { Favourite = false };
        var review = await controller.Sync(TestContext.Current.CancellationToken);
        var removal = Assert.Single(review.Pending);
        await controller.Review(new RemovalDecision(removal.Id, true), TestContext.Current.CancellationToken);
        Assert.Empty(fixture.Loved);
        Assert.Empty((await controller.Get(TestContext.Current.CancellationToken)).Pending);
    }

    [Fact]
    public async Task FavouriteAnswersSayWhetherARunIsStillGoing()
    {
        using var fixture = new FeatureFixture();
        await fixture.InitializeAsync();
        var local = fixture.AddLocal("Loved on Last.fm");
        fixture.Loved.Add(local.Track);
        // The request wait is real time: the test holds a run past it while the run itself keeps going.
        using var runs = new FavouriteRuns(fixture.Favourites, TimeProvider.System, NullLogger<FavouriteRuns>.Instance)
        {
            RequestWait = TimeSpan.FromMilliseconds(50),
        };
        var matching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        fixture.Library.OnMatch = ct => { matching.TrySetResult(); release.Wait(ct); };
        var controller = Attach(new FavouritesController(fixture.Favourites, runs), fixture.Core.UserId);
        var ct = TestContext.Current.CancellationToken;

        var saved = await controller.Enable(new ToggleRequest(true), ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        await matching.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.True(saved.Running);
        Assert.True((await controller.Get(ct)).Running);

        release.Set();
        await runs.RunToEndAsync(fixture.Core.UserId, ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        var ended = await controller.Get(ct);
        Assert.False(ended.Running);
        Assert.NotNull(ended.LastSync);
    }

    [Fact]
    public async Task HistoryPreviewMustBeSavedAndConfirmedBeforeImporting()
    {
        using var fixture = new FeatureFixture();
        await fixture.InitializeAsync();
        var local = fixture.AddLocal("History track", playCount: 2);
        fixture.Top.Add(local.Track with { PlayCount = 10 });
        var discovery = new DiscoveryService(fixture.Api, fixture.Library, Mock.Of<IDiscoveryLibrary>(), fixture.Core.Accounts);
        var controller = Attach(new MusicController(fixture.History, discovery), fixture.Core.UserId);
        Assert.Null(await controller.SavedPreview(cancellationToken: TestContext.Current.CancellationToken));
        var preview = await controller.Preview(TestContext.Current.CancellationToken);
        Assert.Equal(1, preview.MatchedCount);
        Assert.Empty(fixture.Library.HistoryWrites);
        Assert.Equal(preview.Id, (await controller.SavedPreview(cancellationToken: TestContext.Current.CancellationToken))!.Id);
        var result = await controller.Import(new PreviewRequest(preview.Id), TestContext.Current.CancellationToken);
        Assert.Equal(new HistoryImportResult(1, 1, true), result);
        Assert.Equal(10, Assert.Single(fixture.Library.HistoryWrites).Count);
        Assert.Null(await controller.SavedPreview(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeliveryStatusAndRetryCannotSelectAnotherUsersOutbox()
    {
        using var fixture = new FeatureFixture();
        await fixture.InitializeAsync();
        var outbox = new ScrobbleOutbox(fixture.Core.Store, fixture.Core.Accounts, fixture.Core.Client, fixture.Core.Clock);
        var listen = new EligibleListen(Guid.NewGuid(), fixture.Core.UserId,
            new JellySin.Plugin.Lastfm.Playback.MusicTrack(Guid.NewGuid(), "Artist", "Track", null, null, 120), fixture.Core.Clock.GetUtcNow());
        await fixture.Core.Store.WriteAsync(fixture.Core.UserId, "outbox", new OutboxState([new PendingScrobble(listen, BlockedCode: 29)], []), TestContext.Current.CancellationToken);
        using var playback = new PlaybackService(Mock.Of<ISessionManager>(), Mock.Of<IPluginManager>(), new PlaybackTracker(fixture.Core.Clock), outbox, fixture.Core.Accounts, fixture.Core.LibraryRules, fixture.Core.Clock, NullLogger<PlaybackService>.Instance);
        var controller = Attach(new DeliveryController(outbox, playback), fixture.Core.UserId);
        controller.Request.QueryString = new QueryString("?userId=" + Guid.NewGuid());
        var before = JsonSerializer.SerializeToElement(await controller.Status(TestContext.Current.CancellationToken));
        Assert.Equal(1, before.GetProperty("Blocked").GetInt32());
        Assert.Equal(29, before.GetProperty("LastErrorCode").GetInt32());
        Assert.Equal(0, before.GetProperty("OldestPendingDays").GetInt32());
        Assert.IsType<NoContentResult>(await controller.Retry(TestContext.Current.CancellationToken));
        var after = JsonSerializer.SerializeToElement(await controller.Status(TestContext.Current.CancellationToken));
        Assert.Equal(0, after.GetProperty("Blocked").GetInt32());
        Assert.Equal(1, after.GetProperty("Pending").GetInt32());
    }

    [Theory]
    [InlineData(1, 200)]
    [InlineData(2, 200)]
    [InlineData(3, 1)]
    [InlineData(100, 1)]
    public async Task FavouriteReviewResponsesPagePendingChangesAndPreserveTotal(int page, int expectedCount)
    {
        using var fixture = new FeatureFixture();
        await fixture.InitializeAsync();
        var pending = Enumerable.Range(0, 401).Select(index => new FavouriteRemoval(Guid.NewGuid(), Guid.NewGuid(),
            new JellySin.Plugin.Lastfm.Features.MusicTrack("Artist", "Track " + index), "Lastfm")).ToList();
        await fixture.Core.Store.WriteAsync(fixture.Core.UserId, "feature-favourites", new FavouriteState(true, [], pending,
            fixture.Core.Clock.GetUtcNow(), "Review pending changes"), TestContext.Current.CancellationToken);
        var controller = Attach(new FavouritesController(fixture.Favourites, fixture.Runs), fixture.Core.UserId);
        var result = await controller.Get(TestContext.Current.CancellationToken, page);
        Assert.Equal(expectedCount, result.Pending.Count);
        Assert.Equal(401, result.Total);
        Assert.Equal(3, result.Pages);
        Assert.Equal(Math.Min(page, 3), result.Page);
        Assert.True(result.Enabled);
        Assert.Equal("Review pending changes", result.Status);
        Assert.Equal(pending[(Math.Min(page, 3) - 1) * 200].Id, result.Pending[0].Id);
    }

    [Fact]
    public async Task ChartsReturnTheCallersTopTracksForASupportedPeriodOnly()
    {
        using var fixture = new FeatureFixture();
        await fixture.InitializeAsync();
        fixture.Top.Add(new JellySin.Plugin.Lastfm.Features.MusicTrack("Artist", "Chart track", PlayCount: 7));
        var discovery = new DiscoveryService(fixture.Api, fixture.Library, Mock.Of<IDiscoveryLibrary>(), fixture.Core.Accounts);
        var controller = Attach(new MusicController(fixture.History, discovery), fixture.Core.UserId);
        controller.Request.QueryString = new QueryString("?userId=" + Guid.NewGuid());
        var chart = await controller.Charts("7day", TestContext.Current.CancellationToken);
        Assert.Equal("7day", chart.Period);
        var track = Assert.Single(chart.Tracks);
        Assert.Equal(("Artist", "Chart track", 7), (track.Artist, track.Title, track.PlayCount));
        var call = Assert.Single(fixture.Core.Client.Calls);
        Assert.Equal("user.getTopTracks", call.Method);
        Assert.Equal(("TestListener", "7day", "1"), (call.Values["user"], call.Values["period"], call.Values["page"]));
        await Assert.ThrowsAsync<ArgumentException>(() => controller.Charts("1week", TestContext.Current.CancellationToken));
        var stranger = Attach(new MusicController(fixture.History, discovery), Guid.NewGuid());
        await Assert.ThrowsAsync<NotConnectedException>(() => stranger.Charts("7day", TestContext.Current.CancellationToken));
        Assert.Single(fixture.Core.Client.Calls);
    }

    [Fact]
    public async Task StopManagingAnUnknownOrForeignRecipeChangesNothingAndAnswersAlike()
    {
        using var fixture = new FeatureFixture();
        await fixture.InitializeAsync();
        var other = Guid.NewGuid();
        var discovery = new DiscoveryService(fixture.Api, fixture.Library, Mock.Of<IDiscoveryLibrary>(), fixture.Core.Accounts);
        var manager = new Mock<IPlaylistManager>(MockBehavior.Strict);
        var service = new PlaylistService(fixture.Api, discovery, fixture.Library, Mock.Of<ILibraryManager>(), manager.Object,
            fixture.Core.Store, fixture.Locks, fixture.Core.Clock, fixture.Core.Accounts);
        var own = new PlaylistRecipe(Guid.NewGuid(), "Mine", PlaylistSource.Top, PlaylistId: Guid.NewGuid());
        var foreign = new PlaylistRecipe(Guid.NewGuid(), "Theirs", PlaylistSource.Loved, PlaylistId: Guid.NewGuid());
        var pending = new PlaylistRecipe(Guid.NewGuid(), "Theirs pending", PlaylistSource.Top, PlaylistId: Guid.NewGuid());
        var operation = new PlaylistOperation(pending, [Guid.NewGuid()], pending.PlaylistId, Guid.NewGuid());
        await fixture.Core.Store.WriteAsync(fixture.Core.UserId, "feature-playlists", new List<PlaylistRecipe> { own }, TestContext.Current.CancellationToken);
        await fixture.Core.Store.WriteAsync(other, "feature-playlists", new List<PlaylistRecipe> { foreign }, TestContext.Current.CancellationToken);
        await fixture.Core.Store.WriteAsync(other, "feature-playlist-operation", operation, TestContext.Current.CancellationToken);
        var controller = Attach(new PlaylistsController(service), fixture.Core.UserId);
        foreach (var id in new[] { Guid.NewGuid(), foreign.Id, pending.Id })
            Assert.IsType<NoContentResult>(await controller.StopManaging(id, TestContext.Current.CancellationToken));
        Assert.Equal(own.Id, Assert.Single(await controller.Get(TestContext.Current.CancellationToken)).Id);
        Assert.Equal([foreign.Id, pending.Id], (await service.GetRecipesAsync(other, TestContext.Current.CancellationToken)).Select(recipe => recipe.Id));
        Assert.Equal(operation.OperationId, (await service.GetPendingOperationAsync(other, TestContext.Current.CancellationToken))!.OperationId);
        Assert.IsType<NoContentResult>(await controller.StopManaging(own.Id, TestContext.Current.CancellationToken));
        Assert.Empty(await controller.Get(TestContext.Current.CancellationToken));
        manager.VerifyNoOtherCalls();
    }

    private static T Attach<T>(T controller, Guid userId) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.Items[typeof(CallerFilter)] = CallerSecurityTests.Identity(userId);
        return controller;
    }
}
