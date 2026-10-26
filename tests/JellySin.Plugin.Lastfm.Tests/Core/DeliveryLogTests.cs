using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using JellySin.Plugin.Lastfm.Playback;
using JellySin.Plugin.Lastfm.Transport;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class DeliveryLogTests
{
    private const string AccountMarker = "MarkerLastfmAccount";
    private const string UserMarker = "MarkerJellyfinUser";
    private const string ArtistMarker = "MarkerArtist";
    private const string AlbumMarker = "MarkerAlbum";
    private const string TrackMarker = "MarkerTrack";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OneAcceptedPlayWritesInformationLine()
    {
        using var fixture = new CoreFixture();
        var logger = await DeliverScrobblesAsync(
            fixture,
            ["session-1"],
            () => JsonDocument.Parse("{\"scrobbles\":{\"scrobble\":{\"ignoredMessage\":{\"code\":\"0\"}}}}"));

        var expectedUser = Logged.User(fixture.UserId);
        Assert.Single(logger.Lines);
        Assert.Equal((LogLevel.Information, $"Plays submitted for user {expectedUser}: 1 sent, Last.fm accepted 1 and ignored 0 (ignore codes: none)."), logger.Lines[0]);
        AssertNoSecretsOrMarkers(logger.Lines, fixture);
    }

    [Fact]
    public async Task BatchOfTwoWithIgnoreCodesWritesInformationLine()
    {
        using var fixture = new CoreFixture();
        var logger = await DeliverScrobblesAsync(
            fixture,
            ["session-1", "session-2"],
            () => JsonDocument.Parse("{\"scrobbles\":{\"scrobble\":[{\"ignoredMessage\":{\"code\":\"0\"}},{\"ignoredMessage\":{\"code\":\"2\"}}]}}"));

        var expectedUser = Logged.User(fixture.UserId);
        Assert.Single(logger.Lines);
        Assert.Equal((LogLevel.Information, $"Plays submitted for user {expectedUser}: 2 sent, Last.fm accepted 1 and ignored 1 (ignore codes: 2)."), logger.Lines[0]);
        AssertNoSecretsOrMarkers(logger.Lines, fixture);
    }

    [Fact]
    public async Task ScrobbleFailureWritesWarningAndDoesNotRepeatOnNextRun()
    {
        using var fixture = new CoreFixture();
        await ConnectWithMarkerAsync(fixture);
        var (sessions, _, _, outbox, playback, logger) = CreateServices(fixture);
        await playback.StartAsync(TestContext.Current.CancellationToken);
        PlayScrobble(sessions, fixture);
        await playback.StopAsync(TestContext.Current.CancellationToken);
        fixture.Client.Handler = (method, _, _) => method switch
        {
            "track.scrobble" => throw new LastfmException(29),
            _ => JsonDocument.Parse("{}")
        };
        using var delivery = new DeliveryService(playback, outbox, fixture.Accounts, logger);
        await delivery.RunOnceAsync(TestContext.Current.CancellationToken);

        var expectedUser = Logged.User(fixture.UserId);
        Assert.Single(logger.Lines);
        Assert.Equal((LogLevel.Warning, $"Submitting plays for user {expectedUser} failed with Last.fm error 29. All 1 remain stored."), logger.Lines[0]);

        await delivery.RunOnceAsync(TestContext.Current.CancellationToken);
        Assert.Single(logger.Lines);
        AssertNoSecretsOrMarkers(logger.Lines, fixture);
    }

    [Fact]
    public async Task PlayingNowAcceptedWritesInformationLine()
    {
        using var fixture = new CoreFixture();
        await ConnectWithMarkerAsync(fixture);
        var (sessions, _, _, outbox, playback, logger) = CreateServices(fixture);
        await playback.StartAsync(TestContext.Current.CancellationToken);
        PlayNowPlaying(sessions, fixture);
        await playback.StopAsync(TestContext.Current.CancellationToken);
        fixture.Client.Handler = (method, _, _) => method switch
        {
            "track.updateNowPlaying" => JsonDocument.Parse("{\"nowplaying\":{\"ignoredMessage\":{\"code\":\"0\"}}}"),
            _ => JsonDocument.Parse("{}")
        };
        using var delivery = new DeliveryService(playback, outbox, fixture.Accounts, logger);
        await delivery.RunOnceAsync(TestContext.Current.CancellationToken);

        var expectedUser = Logged.User(fixture.UserId);
        Assert.Single(logger.Lines);
        Assert.Equal((LogLevel.Information, $"Playing-now update for user {expectedUser}: Last.fm accepted it."), logger.Lines[0]);
        AssertNoSecretsOrMarkers(logger.Lines, fixture);
    }

    [Fact]
    public async Task PlayingNowIgnoredWritesInformationLine()
    {
        using var fixture = new CoreFixture();
        await ConnectWithMarkerAsync(fixture);
        var (sessions, _, _, outbox, playback, logger) = CreateServices(fixture);
        await playback.StartAsync(TestContext.Current.CancellationToken);
        PlayNowPlaying(sessions, fixture);
        await playback.StopAsync(TestContext.Current.CancellationToken);
        fixture.Client.Handler = (method, _, _) => method switch
        {
            "track.updateNowPlaying" => JsonDocument.Parse("{\"nowplaying\":{\"ignoredMessage\":{\"code\":\"1\"}}}"),
            _ => JsonDocument.Parse("{}")
        };
        using var delivery = new DeliveryService(playback, outbox, fixture.Accounts, logger);
        await delivery.RunOnceAsync(TestContext.Current.CancellationToken);

        var expectedUser = Logged.User(fixture.UserId);
        Assert.Single(logger.Lines);
        Assert.Equal((LogLevel.Information, $"Playing-now update for user {expectedUser}: Last.fm ignored it (ignore code 1)."), logger.Lines[0]);
        AssertNoSecretsOrMarkers(logger.Lines, fixture);
    }

    [Fact]
    public async Task PlayingNowWithoutIgnoreCodeWritesInformationLine()
    {
        using var fixture = new CoreFixture();
        await ConnectWithMarkerAsync(fixture);
        var (sessions, _, _, outbox, playback, logger) = CreateServices(fixture);
        await playback.StartAsync(TestContext.Current.CancellationToken);
        PlayNowPlaying(sessions, fixture);
        await playback.StopAsync(TestContext.Current.CancellationToken);
        fixture.Client.Handler = (method, _, _) => method switch
        {
            "track.updateNowPlaying" => JsonDocument.Parse("{}"),
            _ => JsonDocument.Parse("{}")
        };
        using var delivery = new DeliveryService(playback, outbox, fixture.Accounts, logger);
        await delivery.RunOnceAsync(TestContext.Current.CancellationToken);

        var expectedUser = Logged.User(fixture.UserId);
        Assert.Single(logger.Lines);
        Assert.Equal((LogLevel.Information, $"Playing-now update for user {expectedUser}: Last.fm answered without an error."), logger.Lines[0]);
        AssertNoSecretsOrMarkers(logger.Lines, fixture);
    }

    [Fact]
    public async Task PlayingNowRefusedWritesInformationLine()
    {
        using var fixture = new CoreFixture();
        await ConnectWithMarkerAsync(fixture);
        var (sessions, _, _, outbox, playback, logger) = CreateServices(fixture);
        await playback.StartAsync(TestContext.Current.CancellationToken);
        PlayNowPlaying(sessions, fixture);
        await playback.StopAsync(TestContext.Current.CancellationToken);
        fixture.Client.Handler = (method, _, _) => method switch
        {
            "track.updateNowPlaying" => throw new LastfmException(6),
            _ => JsonDocument.Parse("{}")
        };
        using var delivery = new DeliveryService(playback, outbox, fixture.Accounts, logger);
        await delivery.RunOnceAsync(TestContext.Current.CancellationToken);

        var expectedUser = Logged.User(fixture.UserId);
        Assert.Single(logger.Lines);
        Assert.Equal((LogLevel.Information, $"Playing-now update for user {expectedUser}: Last.fm refused it with error 6. It is not repeated."), logger.Lines[0]);
        AssertNoSecretsOrMarkers(logger.Lines, fixture);
    }

    [Fact]
    public async Task PlayingNowOvertakenByAnotherTrackWritesNoLine()
    {
        using var fixture = new CoreFixture();
        await ConnectWithMarkerAsync(fixture);
        var (sessions, _, tracker, outbox, playback, logger) = CreateServices(fixture);
        await playback.StartAsync(TestContext.Current.CancellationToken);
        PlayNowPlaying(sessions, fixture);
        await playback.StopAsync(TestContext.Current.CancellationToken);
        fixture.Client.Handler = (method, _, _) =>
        {
            if (method == "track.updateNowPlaying")
            {
                tracker.Reset();
                throw new LastfmException(8);
            }
            return JsonDocument.Parse("{}");
        };
        using var delivery = new DeliveryService(playback, outbox, fixture.Accounts, logger);
        await delivery.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(logger.Lines);
        AssertNoSecretsOrMarkers(logger.Lines, fixture);
    }

    [Fact]
    public async Task PlayingNowIgnoredWithANumberAsCodeWritesTheSameLine()
    {
        using var fixture = new CoreFixture();
        var logger = await DeliverPlayingNowAsync(fixture, () => JsonDocument.Parse("{\"nowplaying\":{\"ignoredMessage\":{\"code\":3}}}"));

        var line = Assert.Single(logger.Lines);
        Assert.Equal((LogLevel.Information, $"Playing-now update for user {Logged.User(fixture.UserId)}: Last.fm ignored it (ignore code 3)."), line);
        AssertNoSecretsOrMarkers(logger.Lines, fixture);
    }

    [Fact]
    public async Task PlayingNowRefusedWithErrorEightWhileStillCurrentWritesTheLine()
    {
        using var fixture = new CoreFixture();
        var logger = await DeliverPlayingNowAsync(fixture, () => throw new LastfmException(8));

        var line = Assert.Single(logger.Lines);
        Assert.Equal((LogLevel.Information, $"Playing-now update for user {Logged.User(fixture.UserId)}: Last.fm refused it with error 8. It is not repeated."), line);
        AssertNoSecretsOrMarkers(logger.Lines, fixture);
    }

    [Fact]
    public async Task PlayingNowThatIsTooOldIsNotSentAndWritesNoLine()
    {
        using var fixture = new CoreFixture();
        var logger = await DeliverPlayingNowAsync(fixture, () => JsonDocument.Parse("{}"), secondsBeforeDelivery: 30);

        Assert.Empty(logger.Lines);
        Assert.DoesNotContain(fixture.Client.Calls, call => call.Method == "track.updateNowPlaying");
    }

    [Theory]
    [InlineData(AccountMarker)]
    [InlineData(UserMarker)]
    [InlineData(ArtistMarker)]
    [InlineData(AlbumMarker)]
    [InlineData(TrackMarker)]
    [InlineData("private-session-value")]
    public void TheMarkerCheckRefusesALineThatHoldsAPlantedValue(string planted)
    {
        using var fixture = new CoreFixture();
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertNoSecretsOrMarkers([(LogLevel.Information, $"A line with {planted} in it.")], fixture));
    }

    [Theory]
    [InlineData("D")]
    [InlineData("N")]
    public void TheMarkerCheckRefusesALineThatHoldsTheUserId(string format)
    {
        using var fixture = new CoreFixture();
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            AssertNoSecretsOrMarkers([(LogLevel.Information, $"A line with {fixture.UserId.ToString(format)} in it.")], fixture));
    }

    [Fact]
    public async Task RunWithNothingPendingAndNoPlayingNowWritesNoLine()
    {
        using var fixture = new CoreFixture();
        await ConnectWithMarkerAsync(fixture);
        var (_, _, _, outbox, playback, logger) = CreateServices(fixture);
        using var delivery = new DeliveryService(playback, outbox, fixture.Accounts, logger);
        await delivery.RunOnceAsync(TestContext.Current.CancellationToken);

        Assert.Empty(logger.Lines);
        AssertNoSecretsOrMarkers(logger.Lines, fixture);
    }

    private static (Mock<ISessionManager> Sessions, Mock<IPluginManager> Plugins, PlaybackTracker Tracker, ScrobbleOutbox Outbox, PlaybackService Playback, FakeLogger<DeliveryService> Logger)
        CreateServices(CoreFixture fixture)
    {
        var sessions = new Mock<ISessionManager>();
        var plugins = new Mock<IPluginManager>();
        plugins.SetupGet(value => value.Plugins).Returns([]);
        var tracker = new PlaybackTracker(fixture.Clock);
        var outbox = new ScrobbleOutbox(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock);
        var playback = new PlaybackService(sessions.Object, plugins.Object, tracker, outbox, fixture.Accounts, fixture.LibraryRules, fixture.Clock, NullLogger<PlaybackService>.Instance);
        var logger = new FakeLogger<DeliveryService>();
        return (sessions, plugins, tracker, outbox, playback, logger);
    }

    /// <summary>Connects an account whose Last.fm name is a marker, and sees that the marker is what the server stored.</summary>
    private static async Task ConnectWithMarkerAsync(CoreFixture fixture)
    {
        fixture.Client.Handler = (method, _, _) => method switch
        {
            "auth.getSession" => JsonDocument.Parse($"{{\"session\":{{\"name\":\"{AccountMarker}\",\"key\":\"private-session-value\"}}}}"),
            _ => JsonDocument.Parse("{}")
        };
        await fixture.ConnectAsync();
        Assert.Equal(AccountMarker, (await fixture.Accounts.GetAsync(fixture.UserId, Ct))!.Username);
    }

    [Theory]
    [InlineData("Various Artists", "Various Artists")]
    [InlineData(ArtistMarker, null)]
    [InlineData(null, null)]
    public async Task AlbumArtistReachesLastfmOnlyWhenItDiffersFromTheTrackArtist(string? albumArtist, string? expected)
    {
        using var fixture = new CoreFixture();
        await ConnectWithMarkerAsync(fixture);
        string[] albumArtists = albumArtist is null ? [] : [albumArtist];
        fixture.Client.Handler = (method, _, _) => method switch
        {
            "track.scrobble" => JsonDocument.Parse("{\"scrobbles\":{\"scrobble\":{\"ignoredMessage\":{\"code\":\"0\"}}}}"),
            "track.updateNowPlaying" => JsonDocument.Parse("{\"nowplaying\":{\"ignoredMessage\":{\"code\":\"0\"}}}"),
            _ => JsonDocument.Parse("{}")
        };
        foreach (var play in new Action<Mock<ISessionManager>>[]
        {
            sessions => PlayNowPlaying(sessions, fixture, albumArtists: albumArtists),
            sessions => PlayScrobble(sessions, fixture, albumArtists: albumArtists)
        })
        {
            var (sessions, _, _, outbox, playback, logger) = CreateServices(fixture);
            await playback.StartAsync(Ct);
            play(sessions);
            await playback.StopAsync(Ct);
            using var delivery = new DeliveryService(playback, outbox, fixture.Accounts, logger);
            await delivery.RunOnceAsync(Ct);
            AssertNoSecretsOrMarkers(logger.Lines, fixture);
        }

        var nowPlaying = Assert.Single(fixture.Client.Calls, call => call.Method == "track.updateNowPlaying").Values;
        Assert.Equal(expected, nowPlaying.GetValueOrDefault("albumArtist"));
        var scrobble = Assert.Single(fixture.Client.Calls, call => call.Method == "track.scrobble").Values;
        Assert.Equal(expected, scrobble.GetValueOrDefault("albumArtist[0]"));
    }

    private static async Task<FakeLogger<DeliveryService>> DeliverScrobblesAsync(CoreFixture fixture, string[] sessionIds, Func<JsonDocument> answer)
    {
        await ConnectWithMarkerAsync(fixture);
        var (sessions, _, _, outbox, playback, logger) = CreateServices(fixture);
        await playback.StartAsync(Ct);
        foreach (var sessionId in sessionIds)
        {
            PlayScrobble(sessions, fixture, sessionId);
        }
        await playback.StopAsync(Ct);
        fixture.Client.Handler = (method, _, _) => method switch
        {
            "track.scrobble" => answer(),
            _ => JsonDocument.Parse("{}")
        };
        using var delivery = new DeliveryService(playback, outbox, fixture.Accounts, logger);
        await delivery.RunOnceAsync(Ct);
        return logger;
    }

    private static async Task<FakeLogger<DeliveryService>> DeliverPlayingNowAsync(CoreFixture fixture, Func<JsonDocument> answer, int secondsBeforeDelivery = 0)
    {
        await ConnectWithMarkerAsync(fixture);
        var (sessions, _, _, outbox, playback, logger) = CreateServices(fixture);
        await playback.StartAsync(Ct);
        PlayNowPlaying(sessions, fixture);
        await playback.StopAsync(Ct);
        fixture.Clock.Advance(secondsBeforeDelivery);
        fixture.Client.Handler = (method, _, _) => method == "track.updateNowPlaying" ? answer() : JsonDocument.Parse("{}");
        using var delivery = new DeliveryService(playback, outbox, fixture.Accounts, logger);
        await delivery.RunOnceAsync(Ct);
        return logger;
    }

    private static PlaybackProgressEventArgs CreatePlaybackArgs(CoreFixture fixture, string sessionId = "session-1", string[]? albumArtists = null) => new()
    {
        Item = new Audio
        {
            Id = Guid.NewGuid(),
            Name = TrackMarker,
            Artists = [ArtistMarker],
            Album = AlbumMarker,
            AlbumArtists = albumArtists ?? [],
            RunTimeTicks = TimeSpan.FromSeconds(120).Ticks
        },
        Users = [new User(UserMarker, "auth", "password") { Id = fixture.UserId }],
        PlaySessionId = sessionId,
        PlaybackPositionTicks = 0
    };

    private static void PlayScrobble(Mock<ISessionManager> sessions, CoreFixture fixture, string sessionId = "session-1", string[]? albumArtists = null)
    {
        var args = CreatePlaybackArgs(fixture, sessionId, albumArtists);
        sessions.Raise(value => value.PlaybackStart += null, args);
        fixture.Clock.Advance(30);
        args.PlaybackPositionTicks = TimeSpan.FromSeconds(30).Ticks;
        sessions.Raise(value => value.PlaybackProgress += null, args);
        fixture.Clock.Advance(30);
        args.PlaybackPositionTicks = TimeSpan.FromSeconds(60).Ticks;
        sessions.Raise(value => value.PlaybackProgress += null, args);
        sessions.Raise(value => value.PlaybackStopped += null, new PlaybackStopEventArgs
        {
            Item = args.Item,
            Users = args.Users,
            PlaySessionId = args.PlaySessionId,
            PlaybackPositionTicks = args.PlaybackPositionTicks
        });
    }

    private static void PlayNowPlaying(Mock<ISessionManager> sessions, CoreFixture fixture, string sessionId = "session-now", string[]? albumArtists = null)
    {
        var args = CreatePlaybackArgs(fixture, sessionId, albumArtists);
        sessions.Raise(value => value.PlaybackStart += null, args);
    }

    private static void AssertNoSecretsOrMarkers(IEnumerable<(LogLevel Level, string Text)> lines, CoreFixture fixture)
    {
        var forbidden = new[]
        {
            AccountMarker,
            UserMarker,
            ArtistMarker,
            AlbumMarker,
            TrackMarker,
            "private-session-value",
            fixture.UserId.ToString("D"),
            fixture.UserId.ToString("N")
        };
        foreach (var (_, text) in lines)
        {
            foreach (var marker in forbidden)
            {
                Assert.DoesNotContain(marker, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
