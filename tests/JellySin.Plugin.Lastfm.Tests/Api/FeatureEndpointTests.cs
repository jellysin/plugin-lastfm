using System.Text.Json;
using JellySin.Plugin.Lastfm.Api;
using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Playback;
using JellySin.Plugin.Lastfm.Tests.Features;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Api;

public sealed class FeatureEndpointTests
{
    [Fact]
    public async Task FavouriteEndpointsSyncAndReviewOnlyAuthenticatedUsersState()
    {
        using var fixture = new FeatureFixture();
        await fixture.InitializeAsync();
        var local = fixture.AddLocal("Favourite track", true);
        var controller = Attach(new FavouritesController(fixture.Favourites), fixture.Core.UserId);
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
        Assert.IsType<NoContentResult>(await controller.Import(new PreviewRequest(preview.Id), TestContext.Current.CancellationToken));
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
        using var playback = new PlaybackService(Mock.Of<ISessionManager>(), Mock.Of<IPluginManager>(), new PlaybackTracker(fixture.Core.Clock), outbox, fixture.Core.Clock, NullLogger<PlaybackService>.Instance);
        var controller = Attach(new DeliveryController(outbox, playback), fixture.Core.UserId);
        controller.Request.QueryString = new QueryString("?userId=" + Guid.NewGuid());
        var before = JsonSerializer.SerializeToElement(await controller.Status(TestContext.Current.CancellationToken));
        Assert.Equal(1, before.GetProperty("Blocked").GetInt32());
        Assert.Equal(29, before.GetProperty("LastErrorCode").GetInt32());
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
        var controller = Attach(new FavouritesController(fixture.Favourites), fixture.Core.UserId);
        var result = await controller.Get(TestContext.Current.CancellationToken, page);
        Assert.Equal(expectedCount, result.Pending.Count);
        Assert.Equal(401, result.Total);
        Assert.Equal(3, result.Pages);
        Assert.Equal(Math.Min(page, 3), result.Page);
        Assert.True(result.Enabled);
        Assert.Equal("Review pending changes", result.Status);
        Assert.Equal(pending[(Math.Min(page, 3) - 1) * 200].Id, result.Pending[0].Id);
    }

    private static T Attach<T>(T controller, Guid userId) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.Items[typeof(CallerFilter)] = CallerSecurityTests.Identity(userId);
        return controller;
    }
}
