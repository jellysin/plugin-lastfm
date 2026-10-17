using System.Net;
using System.Text.Json;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Metadata;
using JellySin.Plugin.Lastfm.Tests.Core;
using JellySin.Plugin.Lastfm.Transport;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using Xunit;

namespace JellySin.Plugin.Lastfm.Tests.Metadata;

public sealed class MetadataProviderTests : IDisposable
{
    private readonly CoreFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ArtistBioMappedWithAttribution()
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.Handler = (method, _, _) => JsonDocument.Parse(
            """
            {
              "artist": {
                "name": "Cher",
                "mbid": "bfcc6d75-a6a5-4bc6-8282-47aec8531818",
                "bio": {
                  "content": "Cher is an American singer. <a href=\"http://www.last.fm/music/Cher\">Read more on Last.fm</a>."
                }
              }
            }
            """);

        var provider = new LastfmArtistProvider(_fixture.Client, _fixture.Credentials);
        var info = new ArtistInfo { Name = "Cher" };
        var result = await provider.GetMetadata(info, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.NotNull(result.Item);
        Assert.Equal("Cher", result.Item.Name);
        Assert.Contains("Read more on Last.fm: https://www.last.fm/music/Cher", result.Item.Overview);
        Assert.False(result.QueriedById);
    }

    [Fact]
    public async Task AlbumWikiMappedWithAttribution()
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.Handler = (method, _, _) => JsonDocument.Parse(
            """
            {
              "album": {
                "name": "Believe",
                "artist": "Cher",
                "mbid": "61bf0388-b8a9-48f4-81d1-7eb02706dfb0",
                "wiki": {
                  "content": "Believe is an album by Cher. <a href=\"https://www.last.fm/music/Cher/Believe\">Read more on Last.fm</a>"
                }
              }
            }
            """);

        var provider = new LastfmAlbumProvider(_fixture.Client, _fixture.Credentials);
        var info = new AlbumInfo { Name = "Believe", AlbumArtists = ["Cher"] };
        var result = await provider.GetMetadata(info, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.NotNull(result.Item);
        Assert.Equal("Believe", result.Item.Name);
        Assert.Contains("Read more on Last.fm: https://www.last.fm/music/Cher/Believe", result.Item.Overview);
        Assert.False(result.QueriedById);
    }

    [Fact]
    public async Task HtmlStrippedToPlainText()
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.Handler = (method, _, _) => JsonDocument.Parse(
            """
            {
              "artist": {
                "name": "Cher",
                "bio": {
                  "content": "<p>Cher (<a href=\"https://www.last.fm/music/Cher\">Cher</a>) was <b>born</b> in 1946 &amp; became a star.</p><p>Second paragraph.<br/>Read more on Last.fm <a href=\"http://www.last.fm/music/Cher\">Read more on Last.fm</a>.</p>"
                }
              }
            }
            """);

        var provider = new LastfmArtistProvider(_fixture.Client, _fixture.Credentials);
        var info = new ArtistInfo { Name = "Cher" };
        var result = await provider.GetMetadata(info, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.NotNull(result.Item);
        Assert.DoesNotContain("<p>", result.Item.Overview);
        Assert.DoesNotContain("<b>", result.Item.Overview);
        Assert.DoesNotContain("<br/>", result.Item.Overview);
        Assert.DoesNotContain("&amp;", result.Item.Overview);
        Assert.Contains("Cher (Cher) was born in 1946 & became a star.", result.Item.Overview);
        Assert.Contains("Second paragraph.\nRead more on Last.fm: https://www.last.fm/music/Cher", result.Item.Overview);
    }

    [Fact]
    public async Task MissingBioYieldsNoMetadata()
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.Handler = (method, _, _) => JsonDocument.Parse(
            """
            {
              "artist": {
                "name": "Cher"
              }
            }
            """);

        var provider = new LastfmArtistProvider(_fixture.Client, _fixture.Credentials);
        var info = new ArtistInfo { Name = "Cher" };
        var result = await provider.GetMetadata(info, CancellationToken.None);

        Assert.False(result.HasMetadata);
        Assert.Null(result.Item);
    }

    [Fact]
    public async Task MissingWikiYieldsNoMetadata()
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.Handler = (method, _, _) => JsonDocument.Parse(
            """
            {
              "album": {
                "name": "Believe",
                "artist": "Cher"
              }
            }
            """);

        var provider = new LastfmAlbumProvider(_fixture.Client, _fixture.Credentials);
        var info = new AlbumInfo { Name = "Believe", AlbumArtists = ["Cher"] };
        var result = await provider.GetMetadata(info, CancellationToken.None);

        Assert.False(result.HasMetadata);
        Assert.Null(result.Item);
    }

    [Fact]
    public async Task Error6ReturnsEmptyResult()
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.AsyncHandler = (method, _, _) => Task.FromException<JsonDocument>(new LastfmException(6));

        var provider = new LastfmArtistProvider(_fixture.Client, _fixture.Credentials);
        var info = new ArtistInfo { Name = "UnknownArtistNonExistent" };
        var result = await provider.GetMetadata(info, CancellationToken.None);

        Assert.False(result.HasMetadata);
        Assert.Null(result.Item);
    }

    [Fact]
    public async Task MbidPreferredOverNames_Artist()
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.Handler = (method, _, _) => JsonDocument.Parse(
            """
            {
              "artist": {
                "name": "Cher",
                "mbid": "bfcc6d75-a6a5-4bc6-8282-47aec8531818",
                "bio": { "content": "Bio content. <a href=\"https://www.last.fm/music/Cher\">Read more on Last.fm</a>" }
              }
            }
            """);

        var provider = new LastfmArtistProvider(_fixture.Client, _fixture.Credentials);
        var info = new ArtistInfo
        {
            Name = "Cher",
            ProviderIds = { ["MusicBrainzArtist"] = "bfcc6d75-a6a5-4bc6-8282-47aec8531818" }
        };
        var result = await provider.GetMetadata(info, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.True(result.QueriedById);
        Assert.Single(_fixture.Client.Calls);
        var call = _fixture.Client.Calls[0];
        Assert.Equal("artist.getInfo", call.Method);
        Assert.Equal("bfcc6d75-a6a5-4bc6-8282-47aec8531818", call.Values["mbid"]);
        Assert.Equal("0", call.Values["autocorrect"]);
        Assert.False(call.Values.ContainsKey("artist"));
    }

    [Fact]
    public async Task MbidPreferredOverNames_Album()
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.Handler = (method, _, _) => JsonDocument.Parse(
            """
            {
              "album": {
                "name": "Believe",
                "artist": "Cher",
                "mbid": "61bf0388-b8a9-48f4-81d1-7eb02706dfb0",
                "wiki": { "content": "Album wiki. <a href=\"https://www.last.fm/music/Cher/Believe\">Read more on Last.fm</a>" }
              }
            }
            """);

        var provider = new LastfmAlbumProvider(_fixture.Client, _fixture.Credentials);
        var info = new AlbumInfo
        {
            Name = "Believe",
            AlbumArtists = ["Cher"],
            ProviderIds = { ["MusicBrainzAlbum"] = "61bf0388-b8a9-48f4-81d1-7eb02706dfb0" }
        };
        var result = await provider.GetMetadata(info, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.True(result.QueriedById);
        Assert.Single(_fixture.Client.Calls);
        var call = _fixture.Client.Calls[0];
        Assert.Equal("album.getInfo", call.Method);
        Assert.Equal("61bf0388-b8a9-48f4-81d1-7eb02706dfb0", call.Values["mbid"]);
        Assert.Equal("0", call.Values["autocorrect"]);
        Assert.False(call.Values.ContainsKey("album"));
        Assert.False(call.Values.ContainsKey("artist"));
    }

    [Fact]
    public async Task NamesWithSpacesAndNonAsciiPassedUnchanged()
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.Handler = (method, _, _) => JsonDocument.Parse(
            """
            {
              "artist": {
                "name": "Björk & The Sugar-cubes",
                "bio": { "content": "Bio. <a href=\"https://www.last.fm/music/Bj%C3%B6rk\">Read more on Last.fm</a>" }
              }
            }
            """);

        var provider = new LastfmArtistProvider(_fixture.Client, _fixture.Credentials);
        var info = new ArtistInfo { Name = "Björk & The Sugar-cubes" };
        var result = await provider.GetMetadata(info, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.Single(_fixture.Client.Calls);
        Assert.Equal("Björk & The Sugar-cubes", _fixture.Client.Calls[0].Values["artist"]);
    }

    [Fact]
    public async Task NoCredentialsReturnsEmptyAndMakesNoCall()
    {
        // Unconfigured application credentials
        var provider = new LastfmArtistProvider(_fixture.Client, _fixture.Credentials);
        var info = new ArtistInfo { Name = "Cher" };
        var result = await provider.GetMetadata(info, CancellationToken.None);

        Assert.False(result.HasMetadata);
        Assert.Empty(_fixture.Client.Calls);
    }

    [Fact]
    public async Task CancellationHonoured()
    {
        await _fixture.ConfigureApplicationAsync();
        var provider = new LastfmArtistProvider(_fixture.Client, _fixture.Credentials);
        var info = new ArtistInfo { Name = "Cher" };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetMetadata(info, cts.Token));
    }

    [Fact]
    public async Task WikiPublishedNeverBecomesPremiereDate()
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.Handler = (method, _, _) => JsonDocument.Parse(
            """
            {
              "album": {
                "name": "Believe",
                "artist": "Cher",
                "wiki": {
                  "published": "6 Apr 1999, 00:00",
                  "content": "Album wiki. <a href=\"https://www.last.fm/music/Cher/Believe\">Read more on Last.fm</a>"
                }
              }
            }
            """);

        var provider = new LastfmAlbumProvider(_fixture.Client, _fixture.Credentials);
        var info = new AlbumInfo { Name = "Believe", AlbumArtists = ["Cher"] };
        var result = await provider.GetMetadata(info, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.NotNull(result.Item);
        Assert.Null(result.Item.PremiereDate);
        Assert.Null(result.Item.ProductionYear);
    }

    [Fact]
    public async Task ReleaseDateFieldPopulatesPremiereDateAndProductionYear()
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.Handler = (method, _, _) => JsonDocument.Parse(
            """
            {
              "album": {
                "name": "Believe",
                "artist": "Cher",
                "releasedate": "6 Apr 1999, 00:00",
                "wiki": {
                  "published": "10 May 2010, 12:00",
                  "content": "Album wiki. <a href=\"https://www.last.fm/music/Cher/Believe\">Read more on Last.fm</a>"
                }
              }
            }
            """);

        var provider = new LastfmAlbumProvider(_fixture.Client, _fixture.Credentials);
        var info = new AlbumInfo { Name = "Believe", AlbumArtists = ["Cher"] };
        var result = await provider.GetMetadata(info, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.NotNull(result.Item);
        Assert.Equal(new DateTime(1999, 4, 6), result.Item.PremiereDate);
        Assert.Equal(1999, result.Item.ProductionYear);
    }

    [Fact]
    public async Task GetSearchResultsReturnsAtMostOneResult()
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.Handler = (method, _, _) => JsonDocument.Parse(
            """
            {
              "artist": {
                "name": "Cher",
                "bio": { "content": "Cher bio. <a href=\"https://www.last.fm/music/Cher\">Read more on Last.fm</a>" }
              }
            }
            """);

        var provider = new LastfmArtistProvider(_fixture.Client, _fixture.Credentials);
        var info = new ArtistInfo { Name = "Cher" };
        var results = (await provider.GetSearchResults(info, CancellationToken.None)).ToList();

        Assert.Single(results);
        Assert.Equal("Cher", results[0].Name);
        Assert.Equal("Last.fm", results[0].SearchProviderName);
        Assert.Contains("Read more on Last.fm: https://www.last.fm/music/Cher", results[0].Overview);

        // Also test empty search results when no metadata
        _fixture.Client.Handler = (method, _, _) => JsonDocument.Parse("""{"artist":{"name":"Cher"}}""");
        var emptyResults = (await provider.GetSearchResults(info, CancellationToken.None)).ToList();
        Assert.Empty(emptyResults);
    }

    [Fact]
    public async Task ImagesAreNeverFetched()
    {
        Assert.DoesNotContain(typeof(LastfmArtistProvider).GetConstructors().SelectMany(c => c.GetParameters()), p => p.ParameterType == typeof(IHttpClientFactory));
        Assert.DoesNotContain(typeof(LastfmAlbumProvider).GetConstructors().SelectMany(c => c.GetParameters()), p => p.ParameterType == typeof(IHttpClientFactory));

        var artistProvider = new LastfmArtistProvider(_fixture.Client, _fixture.Credentials);
        var res1 = await artistProvider.GetImageResponse("https://www.last.fm/image.jpg", CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, res1.StatusCode);

        var albumProvider = new LastfmAlbumProvider(_fixture.Client, _fixture.Credentials);
        var res2 = await albumProvider.GetImageResponse("https://lastfm.freetls.fastly.net/image.jpg", CancellationToken.None);
        Assert.Equal(HttpStatusCode.NotFound, res2.StatusCode);
    }

    [Fact]
    public async Task ReleaseGroupIdIsNotSentAsAlbumMbid()
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.Handler = (method, _, _) => JsonDocument.Parse(
            """
            {
              "album": {
                "name": "Believe",
                "artist": "Cher",
                "wiki": { "content": "Album wiki. <a href=\"https://www.last.fm/music/Cher/Believe\">Read more on Last.fm</a>" }
              }
            }
            """);

        var provider = new LastfmAlbumProvider(_fixture.Client, _fixture.Credentials);
        var info = new AlbumInfo
        {
            Name = "Believe",
            AlbumArtists = ["Cher"],
            ProviderIds = { ["MusicBrainzReleaseGroup"] = "4f8773eb-254b-4b13-8a3d-4c3e7215c0e1" }
        };
        var result = await provider.GetMetadata(info, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.False(result.QueriedById);
        Assert.Single(_fixture.Client.Calls);
        var call = _fixture.Client.Calls[0];
        Assert.Equal("album.getInfo", call.Method);
        Assert.Equal("Cher", call.Values["artist"]);
        Assert.Equal("Believe", call.Values["album"]);
        Assert.False(call.Values.ContainsKey("mbid"));
    }

    [Fact]
    public async Task InvalidApiKeyIsNotHidden()
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.AsyncHandler = (method, _, _) => Task.FromException<JsonDocument>(new LastfmException(10));

        var artistProvider = new LastfmArtistProvider(_fixture.Client, _fixture.Credentials);
        var artistInfo = new ArtistInfo { Name = "Cher" };
        var exArtist = await Assert.ThrowsAsync<LastfmException>(() => artistProvider.GetMetadata(artistInfo, CancellationToken.None));
        Assert.Equal(10, exArtist.Code);

        var albumProvider = new LastfmAlbumProvider(_fixture.Client, _fixture.Credentials);
        var albumInfo = new AlbumInfo { Name = "Believe", AlbumArtists = ["Cher"] };
        var exAlbum = await Assert.ThrowsAsync<LastfmException>(() => albumProvider.GetMetadata(albumInfo, CancellationToken.None));
        Assert.Equal(10, exAlbum.Code);
    }

    [Fact]
    public async Task LastfmMbidNeverReachesLibraryIds()
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.Handler = (method, _, _) => JsonDocument.Parse(
            """
            {
              "artist": {
                "name": "Cher",
                "mbid": "11111111-2222-3333-4444-555555555555",
                "bio": { "content": "Cher bio. <a href=\"https://www.last.fm/music/Cher\">Read more on Last.fm</a>" }
              }
            }
            """);

        var artistProvider = new LastfmArtistProvider(_fixture.Client, _fixture.Credentials);
        var artistInfo = new ArtistInfo { Name = "Cher" };
        var artistResult = await artistProvider.GetMetadata(artistInfo, CancellationToken.None);

        Assert.True(artistResult.HasMetadata);
        Assert.NotNull(artistResult.Item);
        Assert.False(artistResult.Item.ProviderIds.ContainsKey("MusicBrainzArtist"));

        var searchResults = (await artistProvider.GetSearchResults(artistInfo, CancellationToken.None)).ToList();
        Assert.Single(searchResults);
        Assert.False(searchResults[0].ProviderIds.ContainsKey("MusicBrainzArtist"));

        _fixture.Client.Handler = (method, _, _) => JsonDocument.Parse(
            """
            {
              "album": {
                "name": "Believe",
                "artist": "Cher",
                "mbid": "22222222-3333-4444-5555-666666666666",
                "wiki": { "content": "Album wiki. <a href=\"https://www.last.fm/music/Cher/Believe\">Read more on Last.fm</a>" }
              }
            }
            """);

        var albumProvider = new LastfmAlbumProvider(_fixture.Client, _fixture.Credentials);
        var albumInfo = new AlbumInfo { Name = "Believe", AlbumArtists = ["Cher"] };
        var albumResult = await albumProvider.GetMetadata(albumInfo, CancellationToken.None);

        Assert.True(albumResult.HasMetadata);
        Assert.NotNull(albumResult.Item);
        Assert.False(albumResult.Item.ProviderIds.ContainsKey("MusicBrainzAlbum"));

        var albumSearchResults = (await albumProvider.GetSearchResults(albumInfo, CancellationToken.None)).ToList();
        Assert.Single(albumSearchResults);
        Assert.False(albumSearchResults[0].ProviderIds.ContainsKey("MusicBrainzAlbum"));
    }

    [Fact]
    public async Task CapKeepsTheAttribution()
    {
        await _fixture.ConfigureApplicationAsync();
        const int cap = 32_768;
        var longBody = new string('x', cap + 500);
        var attributionUrl = "https://www.last.fm/music/Cher";
        var rawContent = $"{longBody} <a href=\"{attributionUrl}\">Read more on Last.fm</a>.";
        var expectedAttribution = $"Read more on Last.fm: {attributionUrl}";
        var jsonContent = JsonSerializer.Serialize(rawContent);

        _fixture.Client.Handler = (method, _, _) => JsonDocument.Parse(
            $$"""
            {
              "artist": {
                "name": "Cher",
                "bio": {
                  "content": {{jsonContent}}
                }
              }
            }
            """);

        var provider = new LastfmArtistProvider(_fixture.Client, _fixture.Credentials);
        var info = new ArtistInfo { Name = "Cher" };
        var result = await provider.GetMetadata(info, CancellationToken.None);

        Assert.True(result.HasMetadata);
        Assert.NotNull(result.Item);
        Assert.NotNull(result.Item.Overview);
        Assert.True(
            result.Item.Overview.Length <= cap + expectedAttribution.Length,
            $"Expected overview length <= {cap + expectedAttribution.Length}, but got {result.Item.Overview.Length}");
        Assert.EndsWith(attributionUrl, result.Item.Overview);
    }
}
