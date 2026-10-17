using System.Globalization;
using System.Net;
using System.Text.Json;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Transport;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace JellySin.Plugin.Lastfm.Metadata;

public sealed class LastfmAlbumProvider(
    ILastfmClient client,
    ApplicationCredentialService credentials) : IRemoteMetadataProvider<MusicAlbum, AlbumInfo>
{
    public string Name => "Last.fm";

    public async Task<MetadataResult<MusicAlbum>> GetMetadata(AlbumInfo info, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var app = await credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!app.IsConfigured || Plugin.Instance?.Configuration.Enabled == false)
            return new MetadataResult<MusicAlbum>();

        var (parameters, queriedById) = BuildParameters(info);
        if (parameters is null)
            return new MetadataResult<MusicAlbum>();

        try
        {
            using var document = await client.CallAsync("album.getInfo", parameters, null, RequestPriority.Background, cancellationToken).ConfigureAwait(false);
            return Parse(document, info, queriedById);
        }
        catch (LastfmException ex) when (ex.Code is 6)
        {
            return new MetadataResult<MusicAlbum>();
        }
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(AlbumInfo searchInfo, CancellationToken cancellationToken)
    {
        var metadata = await GetMetadata(searchInfo, cancellationToken).ConfigureAwait(false);
        if (!metadata.HasMetadata || metadata.Item is null)
            return [];

        var result = new RemoteSearchResult
        {
            Name = metadata.Item.Name,
            SearchProviderName = Name,
            Overview = metadata.Item.Overview,
            PremiereDate = metadata.Item.PremiereDate,
            ProductionYear = metadata.Item.ProductionYear,
            ProviderIds = new Dictionary<string, string>(metadata.Item.ProviderIds, StringComparer.OrdinalIgnoreCase)
        };
        return [result];
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    internal static (Dictionary<string, string>? Parameters, bool QueriedById) BuildParameters(AlbumInfo info)
    {
        if (info.ProviderIds is not null && info.ProviderIds.TryGetValue("MusicBrainzAlbum", out var albumMbid) && !string.IsNullOrWhiteSpace(albumMbid))
        {
            return (new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mbid"] = albumMbid.Trim(),
                ["autocorrect"] = "0"
            }, true);
        }

        var artistName = info.AlbumArtists?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
        if (!string.IsNullOrWhiteSpace(artistName) && !string.IsNullOrWhiteSpace(info.Name))
        {
            return (new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["artist"] = artistName,
                ["album"] = info.Name,
                ["autocorrect"] = "0"
            }, false);
        }

        return (null, false);
    }

    private static MetadataResult<MusicAlbum> Parse(JsonDocument document, AlbumInfo info, bool queriedById)
    {
        var albumNode = MusicJson.Child(document.RootElement, "album");
        if (albumNode.ValueKind != JsonValueKind.Object)
            return new MetadataResult<MusicAlbum>();

        var wikiNode = MusicJson.Child(albumNode, "wiki");
        var overview = MusicJson.CleanWiki(wikiNode);
        if (string.IsNullOrWhiteSpace(overview))
            return new MetadataResult<MusicAlbum> { HasMetadata = false };

        var name = MusicJson.Text(albumNode, "name");
        var item = new MusicAlbum
        {
            Name = string.IsNullOrWhiteSpace(name) ? info.Name : name,
            Overview = overview
        };

        var releaseDateText = MusicJson.Text(albumNode, "releasedate");

        if (!string.IsNullOrWhiteSpace(releaseDateText) &&
            DateTime.TryParse(releaseDateText.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var releaseDate))
        {
            item.PremiereDate = releaseDate;
            item.ProductionYear = releaseDate.Year;
        }

        return new MetadataResult<MusicAlbum>
        {
            HasMetadata = true,
            Item = item,
            QueriedById = queriedById
        };
    }
}
