using Jellyfin.Database.Implementations.Entities;
using JellySin.Plugin.Lastfm.Playback;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class PlaybackLifecycleTests
{
    [Fact]
    public async Task EventBoundaryCapturesListeningThenDrainsAndDetachesAtShutdown()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var sessions = new Mock<ISessionManager>();
        var plugins = new Mock<IPluginManager>();
        plugins.SetupGet(value => value.Plugins).Returns([]);
        var outbox = new ScrobbleOutbox(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock);
        using var playback = new PlaybackService(sessions.Object, plugins.Object, new PlaybackTracker(fixture.Clock), outbox, fixture.Clock, NullLogger<PlaybackService>.Instance);
        await playback.StartAsync(TestContext.Current.CancellationToken);
        var args = Arguments(fixture);
        sessions.Raise(value => value.PlaybackStart += null, args);
        fixture.Clock.Advance(30);
        args.PlaybackPositionTicks = TimeSpan.FromSeconds(30).Ticks;
        sessions.Raise(value => value.PlaybackProgress += null, args);
        fixture.Clock.Advance(30);
        args.PlaybackPositionTicks = TimeSpan.FromSeconds(60).Ticks;
        sessions.Raise(value => value.PlaybackProgress += null, args);
        await playback.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, (await outbox.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Pending);
        Assert.DoesNotContain(fixture.Client.Calls, call => call.Method.StartsWith("track.", StringComparison.Ordinal));
        sessions.VerifyRemove(value => value.PlaybackStart -= It.IsAny<EventHandler<PlaybackProgressEventArgs>>(), Times.Once);
        sessions.VerifyRemove(value => value.PlaybackProgress -= It.IsAny<EventHandler<PlaybackProgressEventArgs>>(), Times.Once);
        Assert.Equal(0, playback.GetStatus().DroppedSnapshots);
        Assert.Equal(0, playback.GetStatus().FailedWrites);
        using var delivery = new DeliveryService(playback, outbox, fixture.Accounts, fixture.Clock, NullLogger<DeliveryService>.Instance);
        await delivery.RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.Single(fixture.Client.Calls, call => call.Method == "track.scrobble");
        Assert.Equal(0, (await outbox.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Pending);
    }

    [Fact]
    public async Task LegacyPluginPreventsAnyNewListening()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var sessions = new Mock<ISessionManager>();
        var plugins = new Mock<IPluginManager>();
        plugins.SetupGet(value => value.Plugins).Returns([new LocalPlugin("legacy", true, new PluginManifest
        { Id = PlaybackService.LegacyPluginId, Name = "Last.fm", Version = "10.11.10.0", Status = MediaBrowser.Model.Plugins.PluginStatus.Active })]);
        var outbox = new ScrobbleOutbox(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock);
        using var playback = new PlaybackService(sessions.Object, plugins.Object, new PlaybackTracker(fixture.Clock), outbox, fixture.Clock, NullLogger<PlaybackService>.Instance);
        await playback.StartAsync(TestContext.Current.CancellationToken);
        sessions.Raise(value => value.PlaybackStart += null, Arguments(fixture));
        await playback.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(playback.GetStatus().LegacyPluginDetected);
        Assert.False(playback.TryTakeNowPlaying(out _));
        using var delivery = new DeliveryService(playback, outbox, fixture.Accounts, fixture.Clock, NullLogger<DeliveryService>.Instance);
        await delivery.RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(fixture.Client.Calls, call => call.Method.StartsWith("track.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MalformedHostEventIsContainedAtCallbackBoundary()
    {
        using var fixture = new CoreFixture();
        var sessions = new Mock<ISessionManager>();
        var plugins = new Mock<IPluginManager>();
        plugins.SetupGet(value => value.Plugins).Returns([]);
        using var playback = new PlaybackService(sessions.Object, plugins.Object, new PlaybackTracker(fixture.Clock),
            new ScrobbleOutbox(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock), fixture.Clock, NullLogger<PlaybackService>.Instance);
        await playback.StartAsync(TestContext.Current.CancellationToken);
        sessions.Raise(value => value.PlaybackStart += null, new PlaybackProgressEventArgs());
        await playback.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, playback.GetStatus().FailedWrites);
    }

    private static PlaybackProgressEventArgs Arguments(CoreFixture fixture) => new()
    {
        Item = new Audio { Id = Guid.NewGuid(), Name = "Track", Artists = ["Artist"], RunTimeTicks = TimeSpan.FromSeconds(120).Ticks },
        Users = [new User("listener", "auth", "password") { Id = fixture.UserId }],
        PlaySessionId = "test-session",
        PlaybackPositionTicks = 0
    };
}
