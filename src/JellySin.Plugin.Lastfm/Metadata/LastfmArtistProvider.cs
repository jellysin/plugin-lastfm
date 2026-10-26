using System.Text.Json;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Transport;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace JellySin.Plugin.Lastfm.Metadata;

public sealed class LastfmArtistProvider(
    ILastfmClient client,
    ApplicationCredentialService credentials) : LastfmInfoProvider<MusicArtist, ArtistInfo>(client, credentials)
{
    protected override string Method => "artist.getInfo";

    protected override (Dictionary<string, string>? Parameters, bool QueriedById) BuildParameters(ArtistInfo info)
    {
        if (info.ProviderIds is not null && info.ProviderIds.TryGetValue("MusicBrainzArtist", out var mbid) && !string.IsNullOrWhiteSpace(mbid))
        {
            return (new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mbid"] = mbid.Trim(),
                ["autocorrect"] = "0"
            }, true);
        }

        if (!string.IsNullOrWhiteSpace(info.Name))
        {
            return (new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["artist"] = info.Name,
                ["autocorrect"] = "0"
            }, false);
        }

        return (null, false);
    }

    protected override MetadataResult<MusicArtist> Parse(JsonDocument document, ArtistInfo info, bool queriedById)
    {
        var artistNode = MusicJson.Child(document.RootElement, "artist");
        if (artistNode.ValueKind != JsonValueKind.Object)
            return new MetadataResult<MusicArtist>();

        var bioNode = MusicJson.Child(artistNode, "bio");
        var overview = MusicJson.CleanWiki(bioNode);
        if (string.IsNullOrWhiteSpace(overview))
            return new MetadataResult<MusicArtist> { HasMetadata = false };

        var name = MusicJson.Text(artistNode, "name");
        var item = new MusicArtist
        {
            Name = string.IsNullOrWhiteSpace(name) ? info.Name : name,
            Overview = overview
        };

        return new MetadataResult<MusicArtist>
        {
            HasMetadata = true,
            Item = item,
            QueriedById = queriedById
        };
    }
}
