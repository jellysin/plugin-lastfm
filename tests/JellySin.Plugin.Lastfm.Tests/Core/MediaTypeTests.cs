using Jellyfin.Database.Implementations.Entities;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Playback;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class MediaTypeTests
{
    public static TheoryData<string, bool, bool, bool, int> Cases => new()
    {
        // Defaults: music only.
        { "music", true, false, false, 1 },
        { "audiobook", true, false, false, 0 },
        { "musicvideo", true, false, false, 0 },
        // Each switch decides its own type and no other.
        { "music", false, true, true, 0 },
        { "audiobook", false, true, false, 1 },
        { "musicvideo", false, false, true, 1 },
        { "audiobook", true, false, true, 0 },
        // A video that is not a music video never counts, whatever is switched on.
        { "video", true, true, true, 0 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task OnlyEnabledMediaTypesAreCaptured(string kind, bool music, bool audiobooks, bool musicVideos, int pending)
    {
        var config = new PluginConfiguration { ScrobbleMusic = music, ScrobbleAudiobooks = audiobooks, ScrobbleMusicVideos = musicVideos };
        Assert.Equal(pending, await PendingAfterPlayAsync(Item(kind), config));
    }

    [Fact]
    public void NewConfigurationScrobblesMusicOnly()
    {
        var config = new PluginConfiguration();
        Assert.True(config.ScrobbleMusic);
        Assert.False(config.ScrobbleAudiobooks);
        Assert.False(config.ScrobbleMusicVideos);
    }

    [Fact]
    public async Task MusicVideoScrobbleCarriesItsArtistAndTitle()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var config = new PluginConfiguration { ScrobbleMusicVideos = true };
        using var rules = new LibraryRuleService(fixture.Store, null, () => config);
        var outbox = new ScrobbleOutbox(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock);
        await PlayAsync(fixture, rules, outbox, Item("musicvideo"));
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        var scrobble = Assert.Single(fixture.Client.Calls, call => call.Method == "track.scrobble").Values;
        Assert.Equal("Artist", scrobble["artist[0]"]);
        Assert.Equal("Title", scrobble["track[0]"]);
        Assert.DoesNotContain("album[0]", scrobble.Keys);
        Assert.DoesNotContain("albumArtist[0]", scrobble.Keys);
    }

    private static async Task<int> PendingAfterPlayAsync(BaseItem item, PluginConfiguration config)
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        using var rules = new LibraryRuleService(fixture.Store, null, () => config);
        var outbox = new ScrobbleOutbox(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock);
        await PlayAsync(fixture, rules, outbox, item);
        return (await outbox.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Pending;
    }

    internal static async Task PlayAsync(CoreFixture fixture, LibraryRuleService rules, ScrobbleOutbox outbox, BaseItem item)
    {
        var sessions = new Mock<ISessionManager>();
        var plugins = new Mock<IPluginManager>();
        plugins.SetupGet(value => value.Plugins).Returns([]);
        using var playback = new PlaybackService(sessions.Object, plugins.Object, new PlaybackTracker(fixture.Clock), outbox, fixture.Accounts, rules, fixture.Clock, NullLogger<PlaybackService>.Instance);
        await playback.StartAsync(TestContext.Current.CancellationToken);
        var args = new PlaybackProgressEventArgs
        {
            Item = item,
            Users = [new User("listener", "auth", "password") { Id = fixture.UserId }],
            PlaySessionId = "media-type-session",
            PlaybackPositionTicks = 0,
        };
        sessions.Raise(value => value.PlaybackStart += null, args);
        for (var elapsed = 0; elapsed < 120; elapsed += 30)
        {
            fixture.Clock.Advance(30);
            args.PlaybackPositionTicks += TimeSpan.FromSeconds(30).Ticks;
            sessions.Raise(value => value.PlaybackProgress += null, args);
        }
        await playback.StopAsync(TestContext.Current.CancellationToken);
    }

    private static BaseItem Item(string kind)
    {
        var ticks = TimeSpan.FromSeconds(200).Ticks;
        return kind switch
        {
            "music" => new Audio { Id = Guid.NewGuid(), Name = "Title", Artists = ["Artist"], AlbumArtists = ["Artist"], RunTimeTicks = ticks },
            "audiobook" => new AudioBook { Id = Guid.NewGuid(), Name = "Chapter 1", Artists = ["Narrator"], RunTimeTicks = ticks },
            "musicvideo" => new MusicVideo { Id = Guid.NewGuid(), Name = "Title", Artists = ["Artist"], RunTimeTicks = ticks },
            _ => new Video { Id = Guid.NewGuid(), Name = "Title", RunTimeTicks = ticks },
        };
    }
}
