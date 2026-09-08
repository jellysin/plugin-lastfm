using System.Text.Json;
using JellySin.Plugin.Lastfm.Features;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Features;

public sealed class DiscoveryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DiscoverySeparatesPlayableTracksAndExternalArtistsAlbums()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var seed = f.AddLocal("Seed");
        var local = f.AddLocal("Local discovery");
        var entities = new Mock<IDiscoveryLibrary>(MockBehavior.Strict);
        entities.Setup(e => e.GetSeed(f.Core.UserId, seed.Id, It.IsAny<CancellationToken>())).Returns(new MusicSeed(seed.Id, seed.Track.Title, seed.Track.Artist, "track"));
        entities.Setup(e => e.MatchEntity(f.Core.UserId, It.IsAny<DiscoveryEntity>(), It.IsAny<CancellationToken>())).Returns<Guid, DiscoveryEntity, CancellationToken>((_, entity, _) => entity);
        f.Core.Client.Handler = (method, _, _) => JsonDocument.Parse(method switch
        {
            "track.getSimilar" => """{"similartracks":{"track":[{"name":"Local discovery","artist":{"name":"Artist"}},{"name":"External discovery","artist":{"name":"New artist"},"url":"https://www.last.fm/music/New+artist/_/External+discovery"}]}}""",
            "artist.getSimilar" => """{"similarartists":{"artist":[{"name":"New artist","url":"https://www.last.fm/music/New+artist"}]}}""",
            "artist.getTopAlbums" => """{"topalbums":{"album":[{"name":"New album","url":"https://www.last.fm/music/New+artist/New+album"}]}}""",
            _ => throw new InvalidOperationException(method),
        });
        var service = new DiscoveryService(f.Api, f.Library, entities.Object, f.Core.Accounts);
        var result = await service.GetAsync(f.Core.UserId, seed.Id, Ct);
        Assert.Equal(local.Id, Assert.Single(result.Local).ItemId);
        Assert.Equal("External discovery", Assert.Single(result.External).Title);
        Assert.Equal("New artist", Assert.Single(result.Artists).Name);
        Assert.Equal("New album", Assert.Single(result.Albums).Name);
        Assert.All(f.Core.Client.Calls, call => Assert.Null(call.Session));
    }

    [Fact]
    public async Task ArtistSeedsAndPersonalSeedsUseTheSupportedPublicMethods()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var id = Guid.NewGuid();
        var entities = new Mock<IDiscoveryLibrary>();
        entities.Setup(e => e.GetSeed(f.Core.UserId, id, It.IsAny<CancellationToken>())).Returns(new MusicSeed(id, "Artist", null, "artist"));
        f.Core.Client.Handler = (method, args, session) => method == "artist.getTopTracks"
            ? FeatureFixture.Page("toptracks", [new MusicTrack("Artist", "Seed")]) : f.Respond(method, args, session);
        var service = new DiscoveryService(f.Api, f.Library, entities.Object, f.Core.Accounts);
        var result = await service.GetAsync(f.Core.UserId, id, Ct);
        Assert.Empty(result.Local);
        Assert.Contains(f.Core.Client.Calls, c => c.Method == "artist.getTopTracks");
        await service.GetAsync(f.Core.UserId, null, Ct);
        Assert.Contains(f.Core.Client.Calls, c => c.Method == "user.getTopTracks" && c.Values["user"] == "TestListener");
    }

    [Fact]
    public async Task OverviewIncludesArtistAlbumAndTrackChartsWithProfileCounts()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        f.Top.Add(new MusicTrack("Artist", "Track", PlayCount: 12));
        f.Core.Client.Handler = (method, args, session) => method switch
        {
            "user.getTopArtists" => JsonDocument.Parse("""{"topartists":{"artist":{"name":"Artist","playcount":"20"}}}"""),
            "user.getTopAlbums" => JsonDocument.Parse("""{"topalbums":{"album":[{"name":"Album","artist":{"name":"Artist"},"playcount":"15"}]}}"""),
            "user.getInfo" => JsonDocument.Parse("""{"user":{"playcount":"50","artist_count":"5","album_count":"8","track_count":"20","url":"https://www.last.fm/user/TestListener"}}"""),
            _ => f.Respond(method, args, session),
        };
        var result = await f.History.GetOverviewAsync(f.Core.UserId, "7day", Ct);
        Assert.Equal("Artist", Assert.Single(result.Artists).Name);
        Assert.Equal("Album", Assert.Single(result.Albums).Name);
        Assert.Equal("Artist", result.Albums[0].Artist);
        Assert.Equal(12, Assert.Single(result.Tracks).PlayCount);
        Assert.Equal(50, result.Statistics.Scrobbles);
        Assert.Equal(5, result.Statistics.Artists);
        Assert.Equal(8, result.Statistics.Albums);
        Assert.Equal(20, result.Statistics.Tracks);
        await Assert.ThrowsAsync<ArgumentException>(() => f.History.GetOverviewAsync(f.Core.UserId, "unsupported", Ct));
    }

    [Fact]
    public async Task SearchPassesTheAuthenticatedUserAndCancellationToTheLibrary()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var entities = new Mock<IDiscoveryLibrary>(MockBehavior.Strict);
        entities.Setup(e => e.Search(f.Core.UserId, "artist", It.IsAny<CancellationToken>())).Returns([new MusicSeed(Guid.NewGuid(), "Artist", null, "artist")]);
        var service = new DiscoveryService(f.Api, f.Library, entities.Object, f.Core.Accounts);
        Assert.Single(await service.SearchSeedsAsync(f.Core.UserId, "artist", Ct));
        Assert.Empty(f.Core.Client.Calls);
    }
}
