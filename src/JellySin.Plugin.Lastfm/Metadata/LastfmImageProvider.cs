using System.Net;
using System.Text.Json;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Transport;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace JellySin.Plugin.Lastfm.Metadata;

/// <summary>
/// Offers the cover of an album and the picture of an artist as Jellyfin remote images. Jellyfin stores an image it
/// chooses in its own library; the plugin keeps no copy.
/// </summary>
public sealed class LastfmImageProvider(
    ILastfmClient client,
    ApplicationCredentialService credentials,
    IHttpClientFactory httpClients,
    TimeProvider time) : IRemoteImageProvider
{
    public string Name => "Last.fm";

    public bool Supports(BaseItem item) => item is MusicArtist or MusicAlbum;

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => Supports(item) ? [ImageType.Primary] : [];

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var app = await credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!app.IsConfigured || Plugin.Instance?.Configuration.Enabled == false)
            return [];

        var (method, node, parameters) = Describe(item);
        if (parameters is null)
            return [];

        try
        {
            using var document = await client.CallAsync(method, parameters, null, RequestPriority.Background, cancellationToken).ConfigureAwait(false);
            var url = LastfmImages.Largest(MusicJson.Child(MusicJson.Child(document.RootElement, node), "image"));
            return url is null ? [] : [new RemoteImageInfo { ProviderName = Name, Url = url, Type = ImageType.Primary }];
        }
        catch (LastfmException ex) when (ex.Code is 6)
        {
            return [];
        }
    }

    public async Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (LastfmImages.Fetchable(url) is not { } uri)
            return new HttpResponseMessage(HttpStatusCode.NotFound);

        var http = httpClients.CreateClient(NamedClient.Default);
        return await LastfmImageDownload.FetchAsync(http, uri, time, cancellationToken).ConfigureAwait(false);
    }

    // The Last.fm call for an item and the name of the node of its answer; the same query the metadata providers make.
    // The lookup info is built from the item's own name and ids: the host's GetLookupInfo() also reads server-wide
    // library settings that the query does not use.
    private static (string Method, string Node, Dictionary<string, string>? Parameters) Describe(BaseItem item) => item switch
    {
        MusicArtist artist => ("artist.getInfo", "artist", LastfmArtistProvider.BuildParameters(
            new ArtistInfo { Name = artist.Name, ProviderIds = new Dictionary<string, string>(artist.ProviderIds, StringComparer.OrdinalIgnoreCase) }).Parameters),
        MusicAlbum album => ("album.getInfo", "album", LastfmAlbumProvider.BuildParameters(
            new AlbumInfo { Name = album.Name, AlbumArtists = album.AlbumArtists, ProviderIds = new Dictionary<string, string>(album.ProviderIds, StringComparer.OrdinalIgnoreCase) }).Parameters),
        _ => (string.Empty, string.Empty, null),
    };
}
