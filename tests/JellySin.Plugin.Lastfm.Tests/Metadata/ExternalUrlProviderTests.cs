using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Metadata;
using MediaBrowser.Controller.Entities.Audio;
using Xunit;

namespace JellySin.Plugin.Lastfm.Tests.Metadata;

public sealed class ExternalUrlProviderTests
{
    private readonly LastfmExternalUrlProvider _provider = new(() => new PluginConfiguration { Enabled = true });

    [Fact]
    public void Name_IsLastfm()
    {
        Assert.Equal("Last.fm", _provider.Name);
    }

    [Fact]
    public void Artist_Cher_ReturnsCanonicalUrl()
    {
        var artist = new MusicArtist { Name = "Cher" };
        var urls = _provider.GetExternalUrls(artist);
        Assert.Equal(["https://www.last.fm/music/Cher"], urls);
    }

    [Fact]
    public void Artist_SigurRos_EncodesSpacesAsPlusAndNonAsciiAsUtf8()
    {
        var artist = new MusicArtist { Name = "Sigur Rós" };
        var urls = _provider.GetExternalUrls(artist);
        Assert.Equal(["https://www.last.fm/music/Sigur+R%C3%B3s"], urls);
    }

    [Fact]
    public void Artist_ACDC_EncodesSlashAsPercent2F()
    {
        var artist = new MusicArtist { Name = "AC/DC" };
        var urls = _provider.GetExternalUrls(artist);
        Assert.Equal(["https://www.last.fm/music/AC%2FDC"], urls);
    }

    [Fact]
    public void Artist_GunsNRoses_EncodesApostropheAsPercent27()
    {
        var artist = new MusicArtist { Name = "Guns N' Roses" };
        var urls = _provider.GetExternalUrls(artist);
        Assert.Equal(["https://www.last.fm/music/Guns+N%27+Roses"], urls);
    }

    [Fact]
    public void Album_BjorkHomogenic_ReturnsCanonicalUrl()
    {
        var album = new MusicAlbum { Name = "Homogenic", AlbumArtists = ["Björk"] };
        var urls = _provider.GetExternalUrls(album);
        Assert.Equal(["https://www.last.fm/music/Bj%C3%B6rk/Homogenic"], urls);
    }

    [Fact]
    public void Album_DaftPunkRandomAccessMemories_EncodesSpacesInArtistAndAlbum()
    {
        var album = new MusicAlbum { Name = "Random Access Memories", AlbumArtists = ["Daft Punk"] };
        var urls = _provider.GetExternalUrls(album);
        Assert.Equal(["https://www.last.fm/music/Daft+Punk/Random+Access+Memories"], urls);
    }

    [Fact]
    public void NamesWithSpecialCharacters_EncodedSafely()
    {
        var artist = new MusicArtist { Name = "Rock + Roll & Jazz? #1" };
        var artistUrls = _provider.GetExternalUrls(artist);
        Assert.Equal(["https://www.last.fm/music/Rock+%2B+Roll+%26+Jazz%3F+%231"], artistUrls);

        var album = new MusicAlbum { Name = "Album #1? & +", AlbumArtists = ["Artist & Co."] };
        var albumUrls = _provider.GetExternalUrls(album);
        Assert.Equal(["https://www.last.fm/music/Artist+%26+Co./Album+%231%3F+%26+%2B"], albumUrls);
    }

    [Fact]
    public void EmptyOrWhitespaceName_ReturnsEmpty()
    {
        Assert.Empty(_provider.GetExternalUrls(new MusicArtist { Name = string.Empty }));
        Assert.Empty(_provider.GetExternalUrls(new MusicArtist { Name = "   " }));
        Assert.Empty(_provider.GetExternalUrls(new MusicAlbum { Name = string.Empty, AlbumArtists = ["Cher"] }));
        Assert.Empty(_provider.GetExternalUrls(new MusicAlbum { Name = "   ", AlbumArtists = ["Cher"] }));
    }

    [Fact]
    public void AlbumWithoutArtist_ReturnsEmpty()
    {
        Assert.Empty(_provider.GetExternalUrls(new MusicAlbum { Name = "Believe" }));
        Assert.Empty(_provider.GetExternalUrls(new MusicAlbum { Name = "Believe", AlbumArtists = [] }));
        Assert.Empty(_provider.GetExternalUrls(new MusicAlbum { Name = "Believe", AlbumArtists = ["   "] }));
        Assert.Empty(_provider.GetExternalUrls(new MusicAlbum { Name = "Believe", AlbumArtists = ["", "  "] }));
    }

    [Fact]
    public void UnsupportedItemType_ReturnsEmpty()
    {
        Assert.Empty(_provider.GetExternalUrls(new Audio { Name = "Believe" }));
    }

    [Fact]
    public void DotOnlyNames_AreEscapedSoTheyStayOneSegment()
    {
        Assert.Equal(["https://www.last.fm/music/%2E%2E"], _provider.GetExternalUrls(new MusicArtist { Name = ".." }));
        Assert.Equal(["https://www.last.fm/music/%2E"], _provider.GetExternalUrls(new MusicArtist { Name = "." }));
        Assert.Equal(
            ["https://www.last.fm/music/Cher/%2E%2E%2E"],
            _provider.GetExternalUrls(new MusicAlbum { Name = "...", AlbumArtists = ["Cher"] }));
        Assert.Equal(["https://www.last.fm/music/Mr.+Big"], _provider.GetExternalUrls(new MusicArtist { Name = "Mr. Big" }));
    }

    [Fact]
    public void PluginDisabled_ReturnsEmpty()
    {
        var provider = new LastfmExternalUrlProvider(() => new PluginConfiguration { Enabled = false });

        Assert.Empty(provider.GetExternalUrls(new MusicArtist { Name = "Cher" }));
        Assert.Empty(provider.GetExternalUrls(new MusicAlbum { Name = "Believe", AlbumArtists = ["Cher"] }));
    }

    [Fact]
    public void NoConfiguration_StillLinks()
    {
        var provider = new LastfmExternalUrlProvider(() => null);

        Assert.Equal(["https://www.last.fm/music/Cher"], provider.GetExternalUrls(new MusicArtist { Name = "Cher" }));
    }
}
