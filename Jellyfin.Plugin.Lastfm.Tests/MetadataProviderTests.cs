using System.Text.Json;
using Jellyfin.Plugin.Lastfm.Providers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Jellyfin.Plugin.Lastfm.Tests;

public sealed class MetadataProviderTests
{
    [Fact]
    public void ImageJsonTextFieldIsMappedAndBestAvailableSizeIsChosen()
    {
        var album = JsonSerializer.Deserialize<LastfmAlbum>("""{"image":[{"#text":"https://images.example/large.png","size":"large"},{"#text":"","size":"mega"},{"#text":"https://images.example/extralarge.png","size":"extralarge"}]}""");
        album.ShouldNotBeNull();
        LastfmHelper.GetImageUrl(album, out var size).ShouldBe("https://images.example/extralarge.png");
        size.ShouldBe("extralarge");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../../outside")]
    [InlineData("C:\\outside")]
    [InlineData("not-a-musicbrainz-id")]
    public void MetadataCannotEscapeTheImageCacheThroughProviderIds(string? id)
    {
        LastfmHelper.GetImageCachePath(Mock.Of<IApplicationPaths>(), id).ShouldBeNull();
    }

    [Fact]
    public void ValidProviderIdIsNormalizedIntoTheImageCache()
    {
        var cache = Path.Combine(Path.GetTempPath(), "lastfm-cache");
        var paths = new Mock<IApplicationPaths>();
        paths.SetupGet(value => value.CachePath).Returns(cache);
        LastfmHelper.GetImageCachePath(paths.Object, "{4E155242-B88B-456D-B790-72565A7FD968}")
            .ShouldBe(Path.Combine(cache, "lastfm", "4e155242-b88b-456d-b790-72565a7fd968", "image.txt"));
    }

    [Fact]
    public async Task ArtistMetadataIsReadFromJsonAndHtmlIsRemoved()
    {
        using var factory = TestHttpClientFactory.Responding("""{"artist":{"similar":{"artist":[{"name":"Another Artist"}]},"stats":{"listeners":"100","playcount":"200"},"bio":{"content":"A <b>great</b> artist","yearformed":"1999","placeformed":"Berlin"}}}""");
        var provider = new LastfmArtistProvider(factory, Mock.Of<IServerConfigurationManager>(), NullLoggerFactory.Instance);
        var result = await provider.GetMetadata(Artist(), TestContext.Current.CancellationToken);
        result.HasMetadata.ShouldBeTrue();
        result.Item.ShouldNotBeNull();
        result.Item.Overview.ShouldBe("A great artist");
        result.Item.ProductionYear.ShouldBe(1999);
        result.Item.ProductionLocations.ShouldContain("Berlin");
        factory.Requests.ShouldHaveSingleItem().Uri.Scheme.ShouldBe("https");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("invalid json")]
    [InlineData("{\"error\":6,\"message\":\"not found\"}")]
    public async Task UnavailableArtistMetadataReturnsAnEmptyResult(string response)
    {
        using var factory = TestHttpClientFactory.Responding(response);
        var provider = new LastfmArtistProvider(factory, Mock.Of<IServerConfigurationManager>(), NullLoggerFactory.Instance);
        (await provider.GetMetadata(Artist(), TestContext.Current.CancellationToken)).HasMetadata.ShouldBeFalse();
    }

    private static ArtistInfo Artist() => new() { Name = "Artist", ProviderIds = new Dictionary<string, string> { ["MusicBrainzArtist"] = "4e155242-b88b-456d-b790-72565a7fd968" } };
}
