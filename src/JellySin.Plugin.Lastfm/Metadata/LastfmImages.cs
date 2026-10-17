using System.Text.Json;
using JellySin.Plugin.Lastfm.Features;

namespace JellySin.Plugin.Lastfm.Metadata;

/// <summary>Which of the image addresses Last.fm lists the plugin offers to Jellyfin, and which address it will fetch.</summary>
internal static class LastfmImages
{
    /// <summary>The only host an image is fetched from: Last.fm's image CDN.</summary>
    public const string Host = "lastfm.freetls.fastly.net";

    // Last.fm's size names from the smallest to the largest.
    private static readonly string[] Sizes = ["small", "medium", "large", "extralarge", "mega"];

    // File-name stems of the images Last.fm shows for an item that has none: the grey star (artist) and the grey disc (album).
    private static readonly string[] Placeholders = ["2a96cbd8b46e442fc41c2b86b821562f", "c6f59c1e5e7240a4c0d427abd71f3dbb"];

    /// <summary>The address when it is https on the image host, with no user information, no other port and no placeholder; otherwise null.</summary>
    public static Uri? Fetchable(string? text)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        var onHost = uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
            && string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase);
        return onHost && !IsPlaceholder(uri) ? uri : null;
    }

    /// <summary>The address of the largest listed image that is fetchable, exactly as Last.fm returned it; null when there is none.</summary>
    public static string? Largest(JsonElement images)
    {
        string? best = null;
        var bestRank = -1;
        foreach (var image in MusicJson.Items(images))
        {
            var size = MusicJson.Text(image, "size");
            var rank = Array.FindIndex(Sizes, name => name.Equals(size, StringComparison.OrdinalIgnoreCase));
            var url = MusicJson.Text(image, "#text");
            if (rank <= bestRank || Fetchable(url) is null) continue;
            (best, bestRank) = (url, rank);
        }

        return best;
    }

    // The stem is the file name up to its first dot, read after percent-decoding, whatever the case.
    private static bool IsPlaceholder(Uri uri)
    {
        var name = Uri.UnescapeDataString(uri.Segments[^1]).Trim('/');
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        var stem = dot < 0 ? name : name[..dot];
        return Placeholders.Contains(stem, StringComparer.OrdinalIgnoreCase);
    }
}
