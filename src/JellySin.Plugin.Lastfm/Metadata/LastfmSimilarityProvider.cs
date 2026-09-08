using System.Runtime.CompilerServices;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Transport;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;

namespace JellySin.Plugin.Lastfm.Metadata;

public sealed class LastfmSimilarityProvider(ILastfmClient client, ApplicationCredentialService credentials) : IRemoteSimilarItemsProvider
{
    public string Name => "JellySin Last.fm";
    public MetadataPluginType Type => MetadataPluginType.SimilarityProvider;
    // The transport alone caches responses using their HTTP policy; host-side caching cannot honor no-store.
    public TimeSpan? CacheDuration => null;
    public bool Supports(Type itemType) => itemType == typeof(Audio) || itemType == typeof(MusicArtist) || itemType == typeof(MusicAlbum);

    public async IAsyncEnumerable<SimilarItemReference> GetSimilarItemsAsync(BaseItem item, SimilarItemsQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.Enabled == false || Plugin.Instance?.Configuration.SimilarityEnabled == false
            || !(await credentials.GetAsync(cancellationToken).ConfigureAwait(false)).IsConfigured)
            yield break;
        var isTrack = item is Audio;
        var isAlbum = item is MusicAlbum;
        var artist = item switch { Audio audio => audio.Artists.FirstOrDefault(), MusicAlbum album => album.AlbumArtists.FirstOrDefault(), _ => item.Name };
        if (string.IsNullOrWhiteSpace(artist)) yield break;
        var kind = isTrack ? "track" : isAlbum ? "album" : "artist";
        var parameters = new Dictionary<string, string> { ["artist"] = artist, ["limit"] = "100", ["autocorrect"] = "0" };
        if (isTrack) parameters["track"] = item.Name;
        // Never add a username or session here: Jellyfin's remote similarity cache is shared between users.
        using var result = await client.CallAsync(isAlbum ? "artist.getTopAlbums" : kind + ".getSimilar", parameters, null, RequestPriority.Background, cancellationToken).ConfigureAwait(false);
        var root = MusicJson.Child(MusicJson.Child(result.RootElement, isAlbum ? "topalbums" : "similar" + kind + "s"), kind);
        foreach (var value in MusicJson.Items(root).Take(100))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Guid.TryParse(MusicJson.Text(value, "mbid"), out var mbid))
            {
                if (item.ProviderIds.Values.Contains(mbid.ToString(), StringComparer.OrdinalIgnoreCase)) continue;
                var score = double.TryParse(MusicJson.Text(value, "match"), System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                    && double.IsFinite(parsed) ? Math.Clamp(parsed, 0, 1) : 0;
                yield return new SimilarItemReference { ProviderName = isTrack ? "MusicBrainzTrack" : isAlbum ? "MusicBrainzAlbum" : "MusicBrainzArtist", ProviderId = mbid.ToString(), Score = isAlbum ? null : (float)score };
            }
        }
    }
}
