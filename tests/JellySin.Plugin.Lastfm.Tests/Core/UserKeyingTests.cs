using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using JellySin.Plugin.Lastfm.Playback;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class UserKeyingTests
{
    [Fact]
    public async Task TwoUsersPlayingAtOnceEachScrobbleWithTheirOwnSession()
    {
        using var fixture = new CoreFixture();
        ConfigureClient(fixture);
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        await fixture.ConnectAsync(userA, "token-a");
        await fixture.ConnectAsync(userB, "token-b");

        var sessions = new Mock<ISessionManager>();
        var outbox = new ScrobbleOutbox(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock);
        using var playback = CreatePlayback(fixture, sessions, outbox);
        await playback.StartAsync(TestContext.Current.CancellationToken);

        var argsA = CreatePlaybackArgs(userA, "session-a", "Track A");
        var argsB = CreatePlaybackArgs(userB, "session-b", "Track B");

        sessions.Raise(value => value.PlaybackStart += null, argsA);
        sessions.Raise(value => value.PlaybackStart += null, argsB);

        fixture.Clock.Advance(30);
        argsA.PlaybackPositionTicks = TimeSpan.FromSeconds(30).Ticks;
        argsB.PlaybackPositionTicks = TimeSpan.FromSeconds(30).Ticks;
        sessions.Raise(value => value.PlaybackProgress += null, argsA);
        sessions.Raise(value => value.PlaybackProgress += null, argsB);

        fixture.Clock.Advance(30);
        argsA.PlaybackPositionTicks = TimeSpan.FromSeconds(60).Ticks;
        argsB.PlaybackPositionTicks = TimeSpan.FromSeconds(60).Ticks;
        sessions.Raise(value => value.PlaybackProgress += null, argsA);
        sessions.Raise(value => value.PlaybackProgress += null, argsB);

        sessions.Raise(value => value.PlaybackStopped += null, StopArgs(argsA));
        sessions.Raise(value => value.PlaybackStopped += null, StopArgs(argsB));

        await playback.StopAsync(TestContext.Current.CancellationToken);
        await outbox.FlushAsync(userA, TestContext.Current.CancellationToken);
        await outbox.FlushAsync(userB, TestContext.Current.CancellationToken);

        var scrobbles = fixture.Client.Calls.Where(call => call.Method == "track.scrobble").ToList();
        Assert.Equal(2, scrobbles.Count);
        var scrobbleA = Assert.Single(scrobbles, call => call.Values.TryGetValue("track[0]", out var track) && track == "Track A");
        var scrobbleB = Assert.Single(scrobbles, call => call.Values.TryGetValue("track[0]", out var track) && track == "Track B");
        Assert.Equal("session-token-a", scrobbleA.Session);
        Assert.Equal("session-token-b", scrobbleB.Session);
        Assert.Equal(0, (await outbox.GetStatusAsync(userA, TestContext.Current.CancellationToken)).Pending);
        Assert.Equal(0, (await outbox.GetStatusAsync(userB, TestContext.Current.CancellationToken)).Pending);
    }

    [Fact]
    public async Task PlayByUnconnectedUserReachesNoOtherAccount()
    {
        using var fixture = new CoreFixture();
        ConfigureClient(fixture);
        var userA = Guid.NewGuid();
        var userC = Guid.NewGuid();
        await fixture.ConnectAsync(userA, "token-a");

        var sessions = new Mock<ISessionManager>();
        var outbox = new ScrobbleOutbox(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock);
        using var playback = CreatePlayback(fixture, sessions, outbox);
        await playback.StartAsync(TestContext.Current.CancellationToken);

        var argsC = CreatePlaybackArgs(userC, "session-c", "Track C");
        PlayToEligibility(sessions, fixture, argsC);

        await playback.StopAsync(TestContext.Current.CancellationToken);
        await outbox.FlushAsync(userA, TestContext.Current.CancellationToken);
        await outbox.FlushAsync(userC, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(fixture.Client.Calls, call => call.Values.TryGetValue("track[0]", out var track) && track == "Track C");
        Assert.Equal(0, (await outbox.GetStatusAsync(userA, TestContext.Current.CancellationToken)).Pending);
        Assert.Equal(0, (await outbox.GetStatusAsync(userC, TestContext.Current.CancellationToken)).Pending);
    }

    [Fact]
    public async Task PlayEventWithMultipleUsersAttributesToEachConnectedAccount()
    {
        using var fixture = new CoreFixture();
        ConfigureClient(fixture);
        var userA = Guid.NewGuid();
        var userB = Guid.NewGuid();
        await fixture.ConnectAsync(userA, "token-a");
        await fixture.ConnectAsync(userB, "token-b");

        var sessions = new Mock<ISessionManager>();
        var outbox = new ScrobbleOutbox(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock);
        using var playback = CreatePlayback(fixture, sessions, outbox);
        await playback.StartAsync(TestContext.Current.CancellationToken);

        var item = new Audio { Id = Guid.NewGuid(), Name = "Shared Track", Artists = ["Artist"], RunTimeTicks = TimeSpan.FromSeconds(120).Ticks };
        var args = new PlaybackProgressEventArgs
        {
            Item = item,
            Users =
            [
                new User("listener-a", "auth", "password") { Id = userA },
                new User("listener-b", "auth", "password") { Id = userB }
            ],
            PlaySessionId = "shared-session",
            PlaybackPositionTicks = 0
        };

        PlayToEligibility(sessions, fixture, args);

        await playback.StopAsync(TestContext.Current.CancellationToken);
        await outbox.FlushAsync(userA, TestContext.Current.CancellationToken);
        await outbox.FlushAsync(userB, TestContext.Current.CancellationToken);

        var scrobbles = fixture.Client.Calls.Where(call => call.Method == "track.scrobble").ToList();
        Assert.Equal(2, scrobbles.Count);
        var scrobbleA = Assert.Single(scrobbles, call => call.Session == "session-token-a");
        var scrobbleB = Assert.Single(scrobbles, call => call.Session == "session-token-b");
        Assert.Equal("Shared Track", scrobbleA.Values["track[0]"]);
        Assert.Equal("Shared Track", scrobbleB.Values["track[0]"]);
        Assert.Equal(0, (await outbox.GetStatusAsync(userA, TestContext.Current.CancellationToken)).Pending);
        Assert.Equal(0, (await outbox.GetStatusAsync(userB, TestContext.Current.CancellationToken)).Pending);
    }

    private static void ConfigureClient(CoreFixture fixture)
    {
        fixture.Client.Handler = (method, parameters, _) => method switch
        {
            "auth.getSession" => JsonDocument.Parse($$$"""{"session":{"name":"Listener-{{{parameters["token"]}}}","key":"session-{{{parameters["token"]}}}"}}"""),
            "track.scrobble" => JsonDocument.Parse("""{"scrobbles":{"scrobble":{"ignoredMessage":{"code":"0"}}}}"""),
            _ => JsonDocument.Parse("{}")
        };
    }

    private static PlaybackService CreatePlayback(CoreFixture fixture, Mock<ISessionManager> sessions, ScrobbleOutbox outbox)
    {
        var plugins = new Mock<IPluginManager>();
        plugins.SetupGet(value => value.Plugins).Returns([]);
        var tracker = new PlaybackTracker(fixture.Clock);
        return new PlaybackService(sessions.Object, plugins.Object, tracker, outbox,
            fixture.Accounts, fixture.LibraryRules, fixture.Clock, NullLogger<PlaybackService>.Instance);
    }

    private static PlaybackProgressEventArgs CreatePlaybackArgs(Guid userId, string sessionId, string trackTitle) => new()
    {
        Item = new Audio { Id = Guid.NewGuid(), Name = trackTitle, Artists = ["Artist"], RunTimeTicks = TimeSpan.FromSeconds(120).Ticks },
        Users = [new User("listener", "auth", "password") { Id = userId }],
        PlaySessionId = sessionId,
        PlaybackPositionTicks = 0
    };

    private static PlaybackStopEventArgs StopArgs(PlaybackProgressEventArgs args) => new()
    {
        Item = args.Item,
        Users = args.Users,
        PlaySessionId = args.PlaySessionId,
        PlaybackPositionTicks = args.PlaybackPositionTicks
    };

    private static void PlayToEligibility(Mock<ISessionManager> sessions, CoreFixture fixture, PlaybackProgressEventArgs args)
    {
        sessions.Raise(value => value.PlaybackStart += null, args);
        fixture.Clock.Advance(30);
        args.PlaybackPositionTicks = TimeSpan.FromSeconds(30).Ticks;
        sessions.Raise(value => value.PlaybackProgress += null, args);
        fixture.Clock.Advance(30);
        args.PlaybackPositionTicks = TimeSpan.FromSeconds(60).Ticks;
        sessions.Raise(value => value.PlaybackProgress += null, args);
        sessions.Raise(value => value.PlaybackStopped += null, StopArgs(args));
    }
}
