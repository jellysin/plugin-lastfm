using System.Globalization;
using System.Text.Json;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Transport;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace JellySin.Plugin.Lastfm.Metadata;

public sealed class LastfmAlbumProvider(
    ILastfmClient client,
    ApplicationCredentialService credentials) : LastfmInfoProvider<MusicAlbum, AlbumInfo>(client, credentials)
{
    protected override string Method => "album.getInfo";

    protected override void Describe(RemoteSearchResult result, MusicAlbum item)
    {
        result.PremiereDate = item.PremiereDate;
        result.ProductionYear = item.ProductionYear;
    }

    protected override (Dictionary<string, string>? Parameters, bool QueriedById) BuildParameters(AlbumInfo info)
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

    protected override MetadataResult<MusicAlbum> Parse(JsonDocument document, AlbumInfo info, bool queriedById)
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
