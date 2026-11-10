using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Transport;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;

namespace JellySin.Plugin.Lastfm.Metadata;

/// <summary>Provides Last.fm artist metadata.</summary>
public sealed class LastfmArtistProvider(
    ILastfmClient client,
    ApplicationCredentialService credentials) : LastfmInfoProvider<MusicArtist, ArtistInfo>(client, credentials)
{
    /// <summary>Gets the Last.fm artist lookup method.</summary>
    protected override string Method => "artist.getInfo";

    /// <summary>Gets the MusicBrainz artist identifier key.</summary>
    protected override string MusicBrainzKey => "MusicBrainzArtist";

    /// <summary>Gets the artist response node name.</summary>
    protected override string ItemNode => "artist";

    /// <summary>Gets the artist biography node name.</summary>
    protected override string TextNode => "bio";

    /// <summary>Builds an artist name query when the name is present.</summary>
    protected override Dictionary<string, string>? NameQuery(ArtistInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.Name))
        {
            return Query(("artist", info.Name));
        }

        return null;
    }
}
