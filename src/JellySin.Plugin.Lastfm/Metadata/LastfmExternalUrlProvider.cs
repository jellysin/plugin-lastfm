using JellySin.Plugin.Lastfm.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;

namespace JellySin.Plugin.Lastfm.Metadata;

/// <summary>
/// Provides external links to Last.fm artist and album pages.
/// Pure function of the item with zero network, database, or disk operations.
/// </summary>
public sealed class LastfmExternalUrlProvider : IExternalUrlProvider
{
    private readonly Func<PluginConfiguration?> _configProvider;

    // Jellyfin activates providers with this constructor and leaves the default; tests pass a configuration so they
    // never set the Plugin.Instance static that parallel tests share.
    public LastfmExternalUrlProvider(Func<PluginConfiguration?>? configProvider = null)
    {
        _configProvider = configProvider ?? (() => Plugin.Instance?.Configuration);
    }

    public string Name => "Last.fm";

    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (_configProvider()?.Enabled == false)
        {
            return [];
        }

        if (item is MusicArtist musicArtist)
        {
            if (string.IsNullOrWhiteSpace(musicArtist.Name))
            {
                return [];
            }

            return [$"https://www.last.fm/music/{EncodeSegment(musicArtist.Name)}"];
        }

        if (item is MusicAlbum album)
        {
            if (string.IsNullOrWhiteSpace(album.Name))
            {
                return [];
            }

            var albumArtist = !string.IsNullOrWhiteSpace(album.AlbumArtist)
                ? album.AlbumArtist
                : album.AlbumArtists?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));

            if (string.IsNullOrWhiteSpace(albumArtist))
            {
                return [];
            }

            return [$"https://www.last.fm/music/{EncodeSegment(albumArtist)}/{EncodeSegment(album.Name)}"];
        }

        return [];
    }

    // Verified against Last.fm canonical URLs (2026-10-17):
    // - https://www.last.fm/music/Sigur+R%C3%B3s
    // - https://www.last.fm/music/Guns+N%27+Roses
    // - https://www.last.fm/music/AC%2FDC
    // - https://www.last.fm/music/Bj%C3%B6rk/Homogenic
    // Last.fm uses '+' for spaces and percent-encodes UTF-8.
    // Uri.EscapeDataString percent-encodes all reserved RFC 3986 characters ('/', '+', '&', '?', '#', ''', etc.)
    // and UTF-8 bytes, leaving spaces as %20 which are then converted to '+'.
    // A name can never inject path segments, query, or fragment. EscapeDataString leaves '.' as is, so a name made
    // only of dots would become a "." or ".." segment that URL normalisation resolves; those dots are escaped.
    private static string EncodeSegment(string segment)
    {
        if (segment.All(c => c == '.'))
        {
            return segment.Replace(".", "%2E", StringComparison.Ordinal);
        }

        return Uri.EscapeDataString(segment).Replace("%20", "+", StringComparison.Ordinal);
    }
}
