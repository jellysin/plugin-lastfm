using System.Globalization;
using System.Text.Json;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Transport;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace JellySin.Plugin.Lastfm.Metadata;

/// <summary>Provides Last.fm album metadata.</summary>
public sealed class LastfmAlbumProvider(
    ILastfmClient client,
    ApplicationCredentialService credentials) : LastfmInfoProvider<MusicAlbum, AlbumInfo>(client, credentials)
{
    /// <summary>Gets the Last.fm album lookup method.</summary>
    protected override string Method => "album.getInfo";

    /// <summary>Gets the MusicBrainz album identifier key.</summary>
    protected override string MusicBrainzKey => "MusicBrainzAlbum";

    /// <summary>Gets the album response node name.</summary>
    protected override string ItemNode => "album";

    /// <summary>Gets the album wiki node name.</summary>
    protected override string TextNode => "wiki";

    /// <summary>Copies album release dates into a search result.</summary>
    protected override void Describe(RemoteSearchResult result, MusicAlbum item)
    {
        result.PremiereDate = item.PremiereDate;
        result.ProductionYear = item.ProductionYear;
    }

    /// <summary>Builds an album name query when artist and album names are present.</summary>
    protected override Dictionary<string, string>? NameQuery(AlbumInfo info)
    {
        var artistName = info.AlbumArtists?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
        if (!string.IsNullOrWhiteSpace(artistName) && !string.IsNullOrWhiteSpace(info.Name))
        {
            return Query(("artist", artistName), ("album", info.Name));
        }

        return null;
    }

    /// <summary>Fills album release dates from the response node.</summary>
    protected override void Fill(MusicAlbum item, JsonElement node)
    {
        var releaseDateText = MusicJson.Text(node, "releasedate");

        if (!string.IsNullOrWhiteSpace(releaseDateText) &&
            DateTime.TryParse(releaseDateText.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var releaseDate))
        {
            item.PremiereDate = releaseDate;
            item.ProductionYear = releaseDate.Year;
        }
    }
}
