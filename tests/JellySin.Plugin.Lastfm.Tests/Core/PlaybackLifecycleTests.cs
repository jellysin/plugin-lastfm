using Jellyfin.Database.Implementations.Entities;
using JellySin.Plugin.Lastfm.Playback;
using JellySin.Plugin.Lastfm.Storage;
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
    public async Task ShutdownRetriesEveryRecoverableListenBeyondTheNormalTickLimit()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var tracker = new PlaybackTracker(fixture.Clock);
        var binding = fixture.Accounts.GetCaptureBinding(fixture.UserId)!;
        for (var index = 0; index < 17; index++)
        {
            var start = new PlaybackSnapshot($"recovery-{index}", fixture.UserId, new MusicTrack(Guid.NewGuid(), "Artist", "Track", null, null, 60),
                PlaybackSignal.Start, 0, false, false, fixture.Clock.GetTimestamp(), fixture.Clock.GetUtcNow(), binding.AccountGeneration, binding.CaptureGeneration);
            tracker.Observe(start);
            fixture.Clock.Advance(30);
            tracker.Observe(start with { Signal = PlaybackSignal.Stop, PositionTicks = TimeSpan.FromSeconds(30).Ticks, MonotonicTimestamp = fixture.Clock.GetTimestamp() });
        }
        Assert.Equal(17, tracker.PendingCount(fixture.UserId));
        var plugins = new Mock<IPluginManager>();
        plugins.SetupGet(value => value.Plugins).Returns([]);
        var outbox = new ScrobbleOutbox(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock);
        using var playback = new PlaybackService(new Mock<ISessionManager>().Object, plugins.Object, tracker, outbox,
            fixture.Accounts, fixture.Clock, NullLogger<PlaybackService>.Instance);
        await playback.StartAsync(TestContext.Current.CancellationToken);
        await playback.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, tracker.PendingCount(fixture.UserId));
        Assert.Equal(17, (await outbox.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Pending);
    }

    [Fact]
    public async Task TransientOutboxWriteFailureRecoversTheEligibleStoppedListen()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var writes = 0;
        var store = new Mock<IStateStore>();
        store.Setup(value => value.UpdateAsync(It.IsAny<Guid>(), "outbox", It.IsAny<Func<OutboxState?, OutboxState>>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, Func<OutboxState?, OutboxState>, CancellationToken>((user, key, update, ct) =>
                ++writes == 1 ? Task.FromException<OutboxState>(new IOException("temporary")) : fixture.Store.UpdateAsync(user, key, update, ct));
        var sessions = new Mock<ISessionManager>();
        var plugins = new Mock<IPluginManager>();
        plugins.SetupGet(value => value.Plugins).Returns([]);
        var tracker = new PlaybackTracker(fixture.Clock);
        var outbox = new ScrobbleOutbox(store.Object, fixture.Accounts, fixture.Client, fixture.Clock);
        using var playback = new PlaybackService(sessions.Object, plugins.Object, tracker, outbox, fixture.Accounts, fixture.Clock, NullLogger<PlaybackService>.Instance);
        await playback.StartAsync(TestContext.Current.CancellationToken);
        var args = Arguments(fixture);
        sessions.Raise(value => value.PlaybackStart += null, args);
        fixture.Clock.Advance(30);
        args.PlaybackPositionTicks = TimeSpan.FromSeconds(30).Ticks;
        sessions.Raise(value => value.PlaybackProgress += null, args);
        fixture.Clock.Advance(30);
        args.PlaybackPositionTicks = TimeSpan.FromSeconds(60).Ticks;
        sessions.Raise(value => value.PlaybackProgress += null, args);
        sessions.Raise(value => value.PlaybackStopped += null, new PlaybackStopEventArgs
        { Item = args.Item, Users = args.Users, PlaySessionId = args.PlaySessionId, PlaybackPositionTicks = args.PlaybackPositionTicks });
        await playback.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, playback.GetStatus().FailedWrites);
        Assert.Equal(0, playback.PendingPersistence(fixture.UserId));
        Assert.False(playback.TryTakeNowPlaying(out _));
        Assert.Single((await fixture.Store.ReadAsync<OutboxState>(fixture.UserId, "outbox", TestContext.Current.CancellationToken))!.Pending);
    }

    [Fact]
    public async Task BufferedSnapshotsAndNowPlayingCannotCrossAReconnect()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new Mock<IStateStore>();
        store.Setup(value => value.UpdateAsync(It.IsAny<Guid>(), "outbox", It.IsAny<Func<OutboxState?, OutboxState>>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, Func<OutboxState?, OutboxState>, CancellationToken>(async (_, _, _, ct) =>
            { reached.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); throw new InvalidOperationException(); });
        var sessions = new Mock<ISessionManager>();
        var plugins = new Mock<IPluginManager>();
        plugins.SetupGet(value => value.Plugins).Returns([]);
        var outbox = new ScrobbleOutbox(store.Object, fixture.Accounts, fixture.Client, fixture.Clock);
        using var playback = new PlaybackService(sessions.Object, plugins.Object, new PlaybackTracker(fixture.Clock), outbox, fixture.Accounts, fixture.Clock, NullLogger<PlaybackService>.Instance);
        await playback.StartAsync(TestContext.Current.CancellationToken);
        var args = Arguments(fixture);
        sessions.Raise(value => value.PlaybackStart += null, args);
        fixture.Clock.Advance(30);
        args.PlaybackPositionTicks = TimeSpan.FromSeconds(30).Ticks;
        sessions.Raise(value => value.PlaybackProgress += null, args);
        fixture.Clock.Advance(30);
        args.PlaybackPositionTicks = TimeSpan.FromSeconds(60).Ticks;
        sessions.Raise(value => value.PlaybackProgress += null, args);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        sessions.Raise(value => value.PlaybackStart += null, Arguments(fixture));
        await fixture.Accounts.DisconnectAsync(fixture.UserId, TestContext.Current.CancellationToken);
        await fixture.ConnectAsync();
        await playback.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(playback.TryTakeNowPlaying(out _));
        Assert.Equal(0, playback.PendingPersistence(fixture.UserId));
        Assert.Null(await fixture.Store.ReadAsync<OutboxState>(fixture.UserId, "outbox", TestContext.Current.CancellationToken));
    }
    [Fact]
    public async Task EventBoundaryCapturesListeningThenDrainsAndDetachesAtShutdown()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var sessions = new Mock<ISessionManager>();
        var plugins = new Mock<IPluginManager>();
        plugins.SetupGet(value => value.Plugins).Returns([]);
        var outbox = new ScrobbleOutbox(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock);
        using var playback = new PlaybackService(sessions.Object, plugins.Object, new PlaybackTracker(fixture.Clock), outbox, fixture.Accounts, fixture.Clock, NullLogger<PlaybackService>.Instance);
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
        using var playback = new PlaybackService(sessions.Object, plugins.Object, new PlaybackTracker(fixture.Clock), outbox, fixture.Accounts, fixture.Clock, NullLogger<PlaybackService>.Instance);
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
            new ScrobbleOutbox(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock), fixture.Accounts, fixture.Clock, NullLogger<PlaybackService>.Instance);
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
