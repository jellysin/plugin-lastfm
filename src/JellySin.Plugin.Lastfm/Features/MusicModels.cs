using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace JellySin.Plugin.Lastfm.Features;

public sealed record MusicTrack(string Artist, string Title, string? Album = null, string? MusicBrainzId = null,
    string? Url = null, int PlayCount = 0, DateTimeOffset? PlayedAt = null, bool NowPlaying = false);

public sealed record MusicPage(IReadOnlyList<MusicTrack> Tracks, int Page, int TotalPages, bool Complete, long? Until = null, int TotalTracks = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] LovedReadGap? Gap = null);

/// <summary>The check a read of the loved tracks failed. The names appear in the server log.</summary>
public enum LovedReadCheck { PageCountChanged, TotalChanged, ItemCountDiffers, FirstPageChanged, DuplicateTracks, EmptyPage, PageLimit }

/// <summary>Why a read of the loved tracks is not trusted, in counts only: no track, artist or user appears.</summary>
public sealed record LovedReadGap(LovedReadCheck Check, int PagesRead, int TotalPages, int FirstTotal, int LatestTotal,
    int ItemsRead, int DistinctItems, FirstPageDifference? FirstPage = null);

/// <summary>How page 1 read again differs from its first read, in counts only; set for the FirstPageChanged check.</summary>
public sealed record FirstPageDifference(int Positions, int PositionsDiffering, bool SameTracks, int TiedDates);

public sealed record MusicChart(string Period, IReadOnlyList<MusicTrack> Tracks);

public sealed record RankedMusic(string Name, string? Artist, string? Url, string? MusicBrainzId, int PlayCount);
public sealed record ListeningStatistics(string Username, int Scrobbles, int Artists, int Albums, int Tracks, string? Url);
public sealed record MusicOverview(string Period, IReadOnlyList<RankedMusic> Artists, IReadOnlyList<RankedMusic> Albums,
    IReadOnlyList<MusicTrack> Tracks, ListeningStatistics Statistics);

public sealed record MusicMatch(MusicTrack Track, Guid? ItemId, string Status);

public sealed record HistoryImportEntry(Guid ItemId, MusicTrack Track, int CurrentPlayCount, int ProposedPlayCount,
    DateTime? CurrentLastPlayed, DateTime? ProposedLastPlayed);

public sealed record HistoryImportPreview(Guid Id, DateTimeOffset ExpiresAt, IReadOnlyList<HistoryImportEntry> Entries,
    IReadOnlyList<MusicMatch> Unmatched, bool Complete, int NextPage = 1, long? Until = null,
    bool CountsComplete = false, bool DatesComplete = false, int RecentNextPage = 1,
    IReadOnlyList<Guid>? DatedItemIds = null, int AppliedCount = 0);

public sealed record HistoryImportResult(int Applied, int Total, bool Complete);

public sealed record MusicSeed(Guid ItemId, string Name, string? Artist, string Kind);
public sealed record DiscoveryEntity(string Kind, string Name, string? Artist, string? Url, string? MusicBrainzId, Guid? ItemId = null);
public sealed record DiscoveryResult(IReadOnlyList<MusicMatch> Local, IReadOnlyList<MusicTrack> External,
    IReadOnlyList<DiscoveryEntity> Artists, IReadOnlyList<DiscoveryEntity> Albums);

public enum PlaylistSource { Loved, Top, Similar, Discovery }

public sealed record PlaylistRecipe(Guid Id, string Name, PlaylistSource Source, string Period = "1month",
    Guid? SeedItemId = null, int Limit = 50, bool DailyRefresh = false, Guid? PlaylistId = null,
    DateTimeOffset? UpdatedAt = null);

internal static class MusicJson
{
    public static JsonElement Child(JsonElement node, string key) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out var result) ? result : default;

    public static string Text(JsonElement node, string key) => Scalar(Child(node, key));

    public static string Scalar(JsonElement node) => node.ValueKind switch
    {
        JsonValueKind.String => node.GetString() ?? string.Empty,
        JsonValueKind.Number => node.GetRawText(),
        JsonValueKind.Object => ObjectText(node),
        _ => string.Empty,
    };

    public static int Number(JsonElement node, string key) =>
        int.TryParse(Text(node, key), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? Math.Max(0, value) : 0;

    public static IEnumerable<JsonElement> Items(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in node.EnumerateArray().Take(201)) yield return child;
        }
        else if (node.ValueKind == JsonValueKind.Object) yield return node;
    }

    public static MusicTrack Track(JsonElement value)
    {
        var date = Child(value, "date");
        DateTimeOffset? played = long.TryParse(Text(date, "uts"), CultureInfo.InvariantCulture, out var seconds)
            && seconds is >= 0 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
        return new(Text(value, "artist"), Text(value, "name"), EmptyNull(Text(value, "album")),
            Guid.TryParse(Text(value, "mbid"), out var id) ? id.ToString() : null,
            SafeUrl(Text(value, "url")), Number(value, "playcount"), played,
            Text(Child(value, "@attr"), "nowplaying") == "true");
    }

    public static string? SafeUrl(string text) => Uri.TryCreate(text, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && (uri.Host == "www.last.fm" || uri.Host == "last.fm")
        && string.IsNullOrEmpty(uri.UserInfo) ? uri.AbsoluteUri : null;

    private static string? EmptyNull(string text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static string ObjectText(JsonElement node)
    {
        var text = Child(node, "#text");
        if (text.ValueKind == JsonValueKind.String && text.GetString() is { Length: > 0 } value) return value;
        var name = Child(node, "name");
        return name.ValueKind == JsonValueKind.String ? name.GetString() ?? string.Empty : string.Empty;
    }

    public const int MaxOverviewLength = 32_768;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly Regex AttributionRegex = new(
        @"(?:\bRead more on Last\.fm\s*)?<a\s+[^>]*href=[""'](?<url>[^""']+)[""'][^>]*>\s*Read more on Last\.fm\.?\s*<\/a>\.?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex AnchorRegex = new(
        @"<a\s+[^>]*>(?<text>.*?)<\/a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex BrRegex = new(
        @"<br\s*/?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex ParagraphCloseRegex = new(
        @"</p>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex AnyTagRegex = new(
        @"<[^>]+>",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private static readonly Regex NewlineRegex = new(
        @"\n{3,}",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    public static string? CleanWiki(JsonElement node)
    {
        var raw = Text(node, "content");
        if (string.IsNullOrWhiteSpace(raw)) raw = Text(node, "summary");
        return CleanWikiText(raw);
    }

    public static string? CleanWikiText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        string? attribution = null;
        var matches = AttributionRegex.Matches(raw);
        if (matches.Count > 0)
        {
            var url = matches[^1].Groups["url"].Value.Trim();
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url[7..];
            else if (url.StartsWith('/'))
                url = "https://www.last.fm" + url;

            var safe = SafeUrl(url);
            attribution = safe is not null ? $"Read more on Last.fm: {safe}" : "Read more on Last.fm";
        }

        var bodyRaw = matches.Count > 0 ? AttributionRegex.Replace(raw, string.Empty) : raw;

        var text = AnchorRegex.Replace(bodyRaw, "${text}");
        text = BrRegex.Replace(text, "\n");
        text = ParagraphCloseRegex.Replace(text, "\n\n");
        text = AnyTagRegex.Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);

        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = text.Split('\n', StringSplitOptions.None);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Contains('\t') || line.Contains("  ", StringComparison.Ordinal))
                line = string.Join(" ", line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            else
                line = line.Trim();
            lines[i] = line;
        }

        text = string.Join("\n", lines);
        text = NewlineRegex.Replace(text, "\n\n").Trim();

        var maxBodyLength = attribution is null ? MaxOverviewLength : MaxOverviewLength - 1;
        if (text.Length > maxBodyLength)
            text = text[..maxBodyLength].TrimEnd();

        if (attribution is not null)
            text = string.IsNullOrWhiteSpace(text) ? attribution : $"{text}\n{attribution}";

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
}
