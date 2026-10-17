using Jellyfin.Database.Implementations.Entities;
using JellySin.Plugin.Lastfm.Playback;
using JellySin.Plugin.Lastfm.Storage;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Core;

/// <summary>Runs the playback service for seven days of simulated time, so its bounds are shown over a server's uptime rather than a burst.</summary>
public sealed class LongRunningSoakTests
{
    private const int Hours = 7 * 24;
    private const int FinishedPerHour = 4;
    // Seven sessions an hour without a stop over 168 hours make 1,176, more than the tracker's 1,024 places.
    private const int AbandonedPerHour = 7;
    private const int FailingHour = Hours - 2;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SevenDaysOfListeningKeepEveryListenWithinTheSessionCapAndKeepRetrying()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var clock = new FakeTimeProvider(fixture.Clock.GetUtcNow());
        var failNext = false;
        var store = new Mock<IStateStore>();
        store.Setup(value => value.UpdateAsync(It.IsAny<Guid>(), "outbox", It.IsAny<Func<OutboxState?, OutboxState>>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, Func<OutboxState?, OutboxState>, CancellationToken>((user, key, update, ct) =>
            {
                if (!failNext) return fixture.Store.UpdateAsync(user, key, update, ct);
                failNext = false;
                return Task.FromException<OutboxState>(new IOException("temporary"));
            });
        var sessions = new Mock<ISessionManager>();
        var plugins = new Mock<IPluginManager>();
        plugins.SetupGet(value => value.Plugins).Returns([]);
        var outbox = new ScrobbleOutbox(store.Object, fixture.Accounts, fixture.Client, clock);
        using var playback = new PlaybackService(sessions.Object, plugins.Object, new PlaybackTracker(clock), outbox, fixture.Accounts, fixture.LibraryRules, clock,
            NullLogger<PlaybackService>.Instance);
        await playback.StartAsync(Ct);
        var user = new User("listener", "auth", "password") { Id = fixture.UserId };
        for (var hour = 0; hour < Hours; hour++)
        {
            failNext = hour == FailingHour;
            for (var listen = 0; listen < FinishedPerHour; listen++) Finish(sessions, clock, user, $"finished-{hour}-{listen}");
            for (var listen = 0; listen < AbandonedPerHour; listen++) Abandon(sessions, clock, user, $"abandoned-{hour}-{listen}");
            clock.Advance(TimeSpan.FromHours(1) - TimeSpan.FromSeconds(FinishedPerHour * 60 + AbandonedPerHour * 10));
            // Only the delivery retry task writes a listen whose first write failed, so the failing hour drains through its retry request.
            await DrainAsync(fixture, clock, playback, (hour + 1) * FinishedPerHour);
        }
        await playback.StopAsync(Ct);
        var status = playback.GetStatus();
        Assert.Equal((0L, 1L), (status.DroppedSnapshots, status.FailedWrites));
        Assert.Equal(Hours * FinishedPerHour, await PendingAsync(fixture));
    }

    private static void Finish(Mock<ISessionManager> sessions, FakeTimeProvider clock, User user, string session)
    {
        var args = Arguments(user, session);
        sessions.Raise(value => value.PlaybackStart += null, args);
        clock.Advance(TimeSpan.FromSeconds(30));
        args.PlaybackPositionTicks = TimeSpan.FromSeconds(30).Ticks;
        sessions.Raise(value => value.PlaybackProgress += null, args);
        clock.Advance(TimeSpan.FromSeconds(30));
        args.PlaybackPositionTicks = TimeSpan.FromSeconds(60).Ticks;
        sessions.Raise(value => value.PlaybackProgress += null, args);
        sessions.Raise(value => value.PlaybackStopped += null, new PlaybackStopEventArgs
        { Item = args.Item, Users = args.Users, PlaySessionId = session, PlaybackPositionTicks = args.PlaybackPositionTicks });
    }

    /// <summary>A client that stops reporting without a stop, as a closed app or lost connection does.</summary>
    private static void Abandon(Mock<ISessionManager> sessions, FakeTimeProvider clock, User user, string session)
    {
        var args = Arguments(user, session);
        sessions.Raise(value => value.PlaybackStart += null, args);
        clock.Advance(TimeSpan.FromSeconds(10));
        args.PlaybackPositionTicks = TimeSpan.FromSeconds(10).Ticks;
        sessions.Raise(value => value.PlaybackProgress += null, args);
    }

    private static async Task DrainAsync(CoreFixture fixture, FakeTimeProvider clock, PlaybackService playback, int expected)
    {
        for (var attempt = 0; attempt < 500 && (await PendingAsync(fixture) < expected || playback.PendingPersistence(fixture.UserId) > 0); attempt++)
        {
            // Stands in for the one-minute delivery retry task, which asks the service to write what it could not store.
            playback.RequestPersistenceRetry();
            clock.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(10, Ct);
        }
        Assert.Equal(expected, await PendingAsync(fixture));
        Assert.Equal(0, playback.PendingPersistence(fixture.UserId));
    }

    private static async Task<int> PendingAsync(CoreFixture fixture)
        => (await fixture.Store.ReadAsync<OutboxState>(fixture.UserId, "outbox", Ct))?.Pending.Count ?? 0;

    private static PlaybackProgressEventArgs Arguments(User user, string session) => new()
    {
        Item = new Audio { Id = Guid.NewGuid(), Name = "Track", Artists = ["Artist"], RunTimeTicks = TimeSpan.FromSeconds(120).Ticks },
        Users = [user],
        PlaySessionId = session,
        PlaybackPositionTicks = 0
    };
}
