using System.Text.RegularExpressions;

namespace JellySin.Plugin.Lastfm.Playback;

/// <summary>Trailing featured credits on titles and artists.</summary>
public static class FeaturedCredit
{
    private static readonly Regex TrailingCredit = new(
        @"(?:(?:\s*-\s*|\s*)[\(\[]\s*\b(?:(?:feat|ft)(?:\.|\b)|featuring\b)\s+[^\)\]]+[\)\]]|(?:\s+-\s*|\s+)\b(?:(?:feat|ft)(?:\.|\b)|featuring\b)\s+[^\(\)\[\]]+)\s*$",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static string Strip(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var match = TrailingCredit.Match(value);
        if (!match.Success) return value;
        var stripped = value[..match.Index].Trim();
        return string.IsNullOrEmpty(stripped) ? value : stripped;
    }
}
