using System.Text.Json;
using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Metadata;
using JellySin.Plugin.Lastfm.Storage;
using JellySin.Plugin.Lastfm.Tests.Core;
using JellySin.Plugin.Lastfm.Transport;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Features;

public sealed class MetadataTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Song", "Artist", "Album", "song", "artist", "album", true)]
    [InlineData("Song", "Artist", "Album", "Song (Live)", "Artist", "Album", false)]
    [InlineData("Song", "Artist", "Album", "Song", "Other", "Album", false)]
    [InlineData("Song", "Artist", "Album", "Song", "Artist", "Compilation", false)]
    [InlineData("Song", "Artist", null, "Song", "Artist", "Compilation", true)]
    public void MatchingDoesNotConfusePerformancesOrArtists(string title, string artist, string? album,
        string candidateTitle, string candidateArtist, string candidateAlbum, bool expected) =>
        Assert.Equal(expected, MusicLibrary.MatchesNames(new(artist, title, album), candidateTitle, [candidateArtist], candidateAlbum));

    [Fact]
    public async Task ArtistMetadataIsBoundedSafeAndReservesNativeStorage()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var mbid = Guid.NewGuid();
        fixture.Client.Handler = (_, _, _) => JsonSerializer.SerializeToDocument(new
        {
            artist = new
            {
                name = "Example",
                mbid,
                url = "https://www.last.fm/music/Example",
                bio = new { summary = "&lt;img src=x onerror=alert(1)&gt;" + new string('a', 400) },
                tags = new { tag = new[] { new { name = "community" }, new { name = "Community" } } }
            },
        });
        var provider = new ArtistMetadataProvider(new(fixture.Client, fixture.Credentials, fixture.Store));
        var result = await provider.GetMetadata(new ArtistInfo { Name = "Example" }, Ct);
        Assert.True(result.HasMetadata);
        Assert.Equal(300, result.Item.Overview.Length);
        Assert.DoesNotContain("<", result.Item.Overview, StringComparison.Ordinal);
        Assert.Single(result.Item.Tags);
        Assert.Empty(result.Item.Genres);
        Assert.Single(Directory.EnumerateFiles(fixture.DirectoryPath, "native-reservations-*.json", SearchOption.AllDirectories));
        Assert.Single(new LastfmExternalUrls().GetExternalUrls(result.Item));
        using var image = await provider.GetImageResponse("https://invalid.example/secret", Ct);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, image.StatusCode);
        Assert.DoesNotContain(fixture.Client.Calls, c => c.Values.ContainsKey("username") || c.Session is not null && !c.Method.StartsWith("auth.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BudgetExhaustionStopsNativeMetadataBeforeItIsReturned()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        using var tinyStore = new FileStateStore(Path.Combine(fixture.DirectoryPath, "small"), 100);
        var api = new MetadataApi(fixture.Client, fixture.Credentials, tinyStore);
        await Assert.ThrowsAsync<StorageBudgetException>(() => api.ReserveNativeCopyAsync("test", new("Name", "Artist", null, null, "Bio", []), Ct));
    }

    [Fact]
    public async Task NativeSimilarityNeverUsesUserIdentityAndRequiresValidIdentifiers()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var valid = Guid.NewGuid();
        fixture.Client.Handler = (_, _, _) => JsonSerializer.SerializeToDocument(new
        { similartracks = new { track = new[] { new { mbid = valid.ToString(), match = "NaN" }, new { mbid = "invalid", match = "1" } } } });
        var provider = new LastfmSimilarityProvider(fixture.Client, fixture.Credentials);
        var values = new List<SimilarItemReference>();
        await foreach (var result in provider.GetSimilarItemsAsync(new Audio { Name = "Song", Artists = ["Artist"] }, new SimilarItemsQuery(), Ct)) values.Add(result);
        Assert.Equal(valid.ToString(), Assert.Single(values).ProviderId);
        Assert.Equal(0, values[0].Score);
        var call = fixture.Client.Calls.Last();
        Assert.Null(call.Session);
        Assert.False(call.Values.ContainsKey("user"));
        Assert.False(call.Values.ContainsKey("username"));
        Assert.Null(provider.CacheDuration);
        Assert.Equal(MetadataPluginType.SimilarityProvider, provider.Type);
        Assert.True(provider.Supports(typeof(Audio)));
        Assert.True(provider.Supports(typeof(MusicAlbum)));
        Assert.True(provider.Supports(typeof(MusicArtist)));
    }

    [Fact]
    public void ExternalLinksRejectCredentialBearingAndForeignUrls()
    {
        var provider = new LastfmExternalUrls();
        foreach (var url in new[] { "http://www.last.fm/music/A", "https://www.last.fm.evil.test/music/A", "https://secret@www.last.fm/music/A", "javascript:alert(1)" })
        {
            var item = new MusicArtist { Name = "A" };
            item.ProviderIds["JellySinLastfm"] = url;
            Assert.Empty(provider.GetExternalUrls(item));
        }
    }

    [Fact]
    public async Task AlbumAndTrackMetadataUseTheirDistinctIdentifiersAndCommunityTags()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var mbid = Guid.NewGuid();
        fixture.Client.Handler = (method, _, _) => JsonSerializer.SerializeToDocument(new Dictionary<string, object>
        {
            [method.StartsWith("album", StringComparison.Ordinal) ? "album" : "track"] = new
            { name = "Title", artist = new { name = "Artist" }, mbid, wiki = new { summary = "Biography &amp; details" }, toptags = new { tag = new { name = "ambient" } } },
        });
        var api = new MetadataApi(fixture.Client, fixture.Credentials, fixture.Store);
        var album = await new AlbumMetadataProvider(api).GetMetadata(new AlbumInfo { Name = "Title", AlbumArtists = ["Artist"] }, Ct);
        var track = await new TrackMetadataProvider(api).GetMetadata(new SongInfo { Name = "Title", Artists = ["Artist"] }, Ct);
        Assert.Equal(mbid.ToString(), album.Item.ProviderIds["MusicBrainzAlbum"]);
        Assert.Equal(mbid.ToString(), track.Item.ProviderIds["MusicBrainzTrack"]);
        Assert.Equal("ambient", Assert.Single(track.Item.Tags));
        Assert.Equal("Biography &amp; details", track.Item.Overview);
        var unavailable = await new TrackMetadataProvider(api).GetMetadata(new SongInfo { Name = "No artist" }, Ct);
        Assert.False(unavailable.HasMetadata);
    }

    [Fact]
    public async Task SearchReturnsBoundedMetadataWithoutImageDownloads()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var mbid = Guid.NewGuid();
        fixture.Client.Handler = (_, _, _) => JsonSerializer.SerializeToDocument(new
        { results = new { albummatches = new { album = new[] { new { name = "Album", artist = "Artist", mbid, url = "https://www.last.fm/music/Artist/Album" } } } } });
        var provider = new AlbumMetadataProvider(new(fixture.Client, fixture.Credentials, fixture.Store));
        var matches = await provider.GetSearchResults(new AlbumInfo { Name = "Album", AlbumArtists = ["Artist"] }, Ct);
        var match = Assert.Single(matches);
        Assert.Equal("Album", match.Name);
        Assert.Equal(mbid.ToString(), match.ProviderIds["MusicBrainzAlbum"]);
        Assert.Equal("20", fixture.Client.Calls.Last().Values["limit"]);
        Assert.Null(match.ImageUrl);
        Assert.Empty(await provider.GetSearchResults(new AlbumInfo { Name = "" }, Ct));
    }

    [Fact]
    public async Task NoStoreResponsesAreNotTransferredToNativeMetadataStorage()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var client = new Mock<ILastfmClient>();
        client.Setup(c => c.CallAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), null,
            It.IsAny<RequestPriority>(), It.IsAny<CancellationToken>())).ReturnsAsync(() => JsonDocument.Parse("""{"artist":{"name":"Do not persist"}}"""));
        client.Setup(c => c.CanPersist(It.IsAny<JsonDocument>())).Returns(false);
        var provider = new ArtistMetadataProvider(new(client.Object, fixture.Credentials, fixture.Store));
        var result = await provider.GetMetadata(new ArtistInfo { Name = "Artist" }, Ct);
        Assert.False(result.HasMetadata);
        Assert.Empty(Directory.EnumerateFiles(fixture.DirectoryPath, "native-reservations-*.json", SearchOption.AllDirectories));
    }
}
