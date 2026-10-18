namespace JellySin.Plugin.Lastfm.Playback;

/// <summary>The artist name a listen is sent under.</summary>
/// <remarks>
/// Jellyfin keeps the tag's artists as a split list, not the credit as written, so the joined form is
/// rebuilt as "A, B &amp; C". A credit that would be cut short is not sent; the first artist is sent instead.
/// </remarks>
public static class ArtistCredit
{
    public const int MaxLength = 1024;
    public const int MaxJoinedArtists = 16;

    public static string? Format(IReadOnlyList<string> artists, bool joined)
    {
        var first = artists.Count > 0 ? artists[0] : null;
        if (string.IsNullOrWhiteSpace(first) || first.Length > MaxLength) return null;
        if (!joined || artists.Count > MaxJoinedArtists) return first;
        var names = artists.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length < 2) return first;
        var credit = string.Join(", ", names[..^1]) + " & " + names[^1];
        return credit.Length <= MaxLength ? credit : first;
    }
}
