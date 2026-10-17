using System.Text.Json;
using JellySin.Plugin.Lastfm.Metadata;
using JellySin.Plugin.Lastfm.Tests.Core;
using JellySin.Plugin.Lastfm.Transport;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Entities;
using static JellySin.Plugin.Lastfm.Tests.Metadata.FakeImageHost;

namespace JellySin.Plugin.Lastfm.Tests.Metadata;

public sealed class ImageProviderTests : IDisposable
{
    private readonly CoreFixture _fixture = new();
    private readonly FakeImageHost _host = new();

    public void Dispose() => _fixture.Dispose();

    private LastfmImageProvider Provider() => new(_fixture.Client, _fixture.Credentials, _host, _fixture.Clock);

    private static MusicArtist Artist(string name = "Cher", string? mbid = null)
    {
        var artist = new MusicArtist { Name = name };
        if (mbid is not null) artist.ProviderIds["MusicBrainzArtist"] = mbid;
        return artist;
    }

    private static MusicAlbum Album(string name = "Believe", string artist = "Cher", string? mbid = null)
    {
        var album = new MusicAlbum { Name = name, AlbumArtists = [artist] };
        if (mbid is not null) album.ProviderIds["MusicBrainzAlbum"] = mbid;
        return album;
    }

    [Fact]
    public async Task ArtistGetsOnlyTheLargestSizeExactlyAsListed()
    {
        await _fixture.ConfigureApplicationAsync();
        var mega = Url(Real, "arQ/mega", "jpg") + "?v=1";
        _fixture.Client.Handler = (_, _, _) => Info("artist", ("small", Url("a1")), ("mega", mega), ("large", Url("a3")), ("medium", Url("a4")), ("extralarge", Url("a5")));

        var images = (await Provider().GetImages(Artist(), TestContext.Current.CancellationToken)).ToList();

        var image = Assert.Single(images);
        Assert.Equal(mega, image.Url);
        Assert.Equal("Last.fm", image.ProviderName);
        Assert.Equal(ImageType.Primary, image.Type);
        Assert.Equal("artist.getInfo", Assert.Single(_fixture.Client.Calls).Method);
    }

    [Fact]
    public async Task AlbumGetsOnlyTheLargestSizeAndSizeOrderBeatsListOrder()
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.Handler = (_, _, _) => Info("album", ("extralarge", Url("b1")), ("large", Url("b2")), ("small", Url("b3")));

        var image = Assert.Single(await Provider().GetImages(Album(), TestContext.Current.CancellationToken));

        Assert.Equal(Url("b1"), image.Url);
        Assert.Equal("album.getInfo", Assert.Single(_fixture.Client.Calls).Method);
    }

    [Fact]
    public async Task ArtistStarPlaceholderIsNotOffered()
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.Handler = (_, _, _) => Info("artist", ("small", Url(Star, "34s")), ("large", Url(Star, "174s")), ("mega", Url(Star, "300x300")));

        Assert.Empty(await Provider().GetImages(Artist(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AlbumDiscPlaceholderIsNotOffered()
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.Handler = (_, _, _) => Info("album", ("small", Url(Disc, "34s")), ("extralarge", Url(Disc, "300x300")));

        Assert.Empty(await Provider().GetImages(Album(), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("2A96CBD8B46E442FC41C2B86B821562F", "png")]
    [InlineData("C6F59C1E5E7240A4C0D427ABD71F3DBB", "jpg")]
    [InlineData("2a96cbd8b46e442fc41c2b86b821562f", "gif")]
    [InlineData("c6f59c1e5e7240a4c0d427abd71f3dbb", "webp")]
    public async Task PlaceholderIsKnownByItsStemInAnyCaseAndExtension(string hash, string extension)
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.Handler = (_, _, _) => Info("artist", ("mega", Url(hash, "300x300", extension)));

        Assert.Empty(await Provider().GetImages(Artist(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ALargestPlaceholderIsSkippedButARealSmallerImageIsKept()
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.Handler = (_, _, _) => Info("album", ("mega", Url(Disc)), ("extralarge", Url(Real, "300x300", "jpg")));

        var image = Assert.Single(await Provider().GetImages(Album(), TestContext.Current.CancellationToken));

        Assert.Equal(Url(Real, "300x300", "jpg"), image.Url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://lastfm.freetls.fastly.net/i/u/300x300/0f1e2d3c4b5a69788796a5b4c3d2e1f0.png")]
    [InlineData("https://example.org/i/u/300x300/0f1e2d3c4b5a69788796a5b4c3d2e1f0.png")]
    [InlineData("https://lastfm.freetls.fastly.net@evil.example/x.png")]
    [InlineData("https://lastfm.freetls.fastly.net:8443/i/u/300x300/0f1e2d3c4b5a69788796a5b4c3d2e1f0.png")]
    [InlineData("https://user:secret@lastfm.freetls.fastly.net/i/u/300x300/0f1e2d3c4b5a69788796a5b4c3d2e1f0.png")]
    [InlineData("https://lastfm.freetls.fastly.net.evil.example/i/u/300x300/0f1e2d3c4b5a69788796a5b4c3d2e1f0.png")]
    [InlineData("/i/u/300x300/0f1e2d3c4b5a69788796a5b4c3d2e1f0.png")]
    public async Task AnAddressThatIsNotHttpsOnTheImageHostIsNotOffered(string url)
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.Handler = (_, _, _) => Info("artist", ("mega", url));

        Assert.Empty(await Provider().GetImages(Artist(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUnusableLargestImageDoesNotHideAUsableSmallerOne()
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.Handler = (_, _, _) => Info("artist", ("mega", ""), ("extralarge", "http://lastfm.freetls.fastly.net/x.png"), ("large", Url(Real, "174s", "jpg")));

        var image = Assert.Single(await Provider().GetImages(Artist(), TestContext.Current.CancellationToken));

        Assert.Equal(Url(Real, "174s", "jpg"), image.Url);
    }

    [Fact]
    public async Task AnswerWithoutImagesGivesNone()
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.Handler = (_, _, _) => JsonDocument.Parse("""{ "album": { "name": "Believe" } }""");

        Assert.Empty(await Provider().GetImages(Album(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MbidIsPreferredForAnArtist()
    {
        await _fixture.ConfigureApplicationAsync();
        const string mbid = "bfcc6d75-a6a5-4bc6-8282-47aec8531818";

        await Provider().GetImages(Artist(mbid: mbid), TestContext.Current.CancellationToken);

        var values = Assert.Single(_fixture.Client.Calls).Values;
        Assert.Equal(mbid, values["mbid"]);
        Assert.False(values.ContainsKey("artist"));
        Assert.Equal("0", values["autocorrect"]);
    }

    [Fact]
    public async Task MbidIsPreferredForAnAlbum()
    {
        await _fixture.ConfigureApplicationAsync();
        const string mbid = "61bf0388-b8a9-48f4-81d1-7eb02706dfb0";

        await Provider().GetImages(Album(mbid: mbid), TestContext.Current.CancellationToken);

        var values = Assert.Single(_fixture.Client.Calls).Values;
        Assert.Equal(mbid, values["mbid"]);
        Assert.False(values.ContainsKey("album"));
        Assert.Equal("0", values["autocorrect"]);
    }

    [Fact]
    public async Task NamesWithSpacesAndNonAsciiArePassedUnchanged()
    {
        await _fixture.ConfigureApplicationAsync();

        await Provider().GetImages(Artist("Björk & The Sugar-cubes"), TestContext.Current.CancellationToken);
        await Provider().GetImages(Album("Dancer in the Dark – Selmasongs", "Björk"), TestContext.Current.CancellationToken);

        Assert.Equal("Björk & The Sugar-cubes", _fixture.Client.Calls[0].Values["artist"]);
        Assert.Equal("Björk", _fixture.Client.Calls[1].Values["artist"]);
        Assert.Equal("Dancer in the Dark – Selmasongs", _fixture.Client.Calls[1].Values["album"]);
        Assert.Equal("0", _fixture.Client.Calls[1].Values["autocorrect"]);
    }

    [Fact]
    public async Task NoCredentialsGivesNoImageAndMakesNoCall()
    {
        Assert.Empty(await Provider().GetImages(Artist(), TestContext.Current.CancellationToken));
        Assert.Empty(await Provider().GetImages(Album(), TestContext.Current.CancellationToken));

        Assert.Empty(_fixture.Client.Calls);
    }

    [Fact]
    public async Task Error6GivesNoImage()
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.AsyncHandler = (_, _, _) => Task.FromException<JsonDocument>(new LastfmException(6));

        Assert.Empty(await Provider().GetImages(Artist(), TestContext.Current.CancellationToken));
        Assert.Empty(await Provider().GetImages(Album(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Error10IsNotHidden()
    {
        await _fixture.ConfigureApplicationAsync();
        _fixture.Client.AsyncHandler = (_, _, _) => Task.FromException<JsonDocument>(new LastfmException(10));

        var artist = await Assert.ThrowsAsync<LastfmException>(() => Provider().GetImages(Artist(), TestContext.Current.CancellationToken));
        var album = await Assert.ThrowsAsync<LastfmException>(() => Provider().GetImages(Album(), TestContext.Current.CancellationToken));

        Assert.Equal((10, 10), (artist.Code, album.Code));
    }

    [Fact]
    public async Task CancellationIsHonouredBeforeAnyCall()
    {
        await _fixture.ConfigureApplicationAsync();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider().GetImages(Artist(), cancelled.Token));

        Assert.Empty(_fixture.Client.Calls);
    }

    [Fact]
    public async Task CancellationReachesTheLastfmCall()
    {
        await _fixture.ConfigureApplicationAsync();
        using var cancelled = new CancellationTokenSource();
        CancellationToken seen = default;
        _fixture.Client.AsyncHandler = async (_, _, token) =>
        {
            seen = token;
            await cancelled.CancelAsync();
            token.ThrowIfCancellationRequested();
            return JsonDocument.Parse("{}");
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider().GetImages(Album(), cancelled.Token));

        Assert.Equal(cancelled.Token, seen);
    }

    [Fact]
    public void OnlyAlbumsAndArtistsAreServedAndOnlyAsPrimary()
    {
        var provider = Provider();

        Assert.Equal("Last.fm", provider.Name);
        Assert.True(provider.Supports(Artist()));
        Assert.True(provider.Supports(Album()));
        Assert.False(provider.Supports(new Audio()));
        Assert.Equal([ImageType.Primary], provider.GetSupportedImages(Artist()));
        Assert.Equal([ImageType.Primary], provider.GetSupportedImages(Album()));
        Assert.Empty(provider.GetSupportedImages(new Audio()));
    }

    [Fact]
    public async Task AnItemTheProviderDoesNotServeGetsNoImageAndNoCall()
    {
        await _fixture.ConfigureApplicationAsync();

        Assert.Empty(await Provider().GetImages(new Audio { Name = "Believe" }, TestContext.Current.CancellationToken));

        Assert.Empty(_fixture.Client.Calls);
    }
}
