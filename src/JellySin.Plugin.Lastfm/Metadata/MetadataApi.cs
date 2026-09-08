using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Storage;
using JellySin.Plugin.Lastfm.Transport;

namespace JellySin.Plugin.Lastfm.Metadata;

public sealed record MusicMetadata(string Name, string Artist, string? MusicBrainzId, string? Url, string Overview, string[] Tags);

public sealed partial class MetadataApi(ILastfmClient client, ApplicationCredentialService credentials, IStateStore store)
{
    public async Task<MusicMetadata?> GetAsync(string kind, string name, string? artist, string? mbid, CancellationToken ct)
    {
        if (!(await credentials.GetAsync(ct).ConfigureAwait(false)).IsConfigured) return null;
        var parameters = Parameters(kind, name, artist, mbid);
        if (parameters is null) return null;
        try
        {
            using var result = await client.CallAsync(kind + ".getInfo", parameters, null, RequestPriority.Background, ct).ConfigureAwait(false);
            if (!client.CanPersist(result)) return null;
            var node = MusicJson.Child(result.RootElement, kind);
            return node.ValueKind == JsonValueKind.Object ? Parse(kind, node) : null;
        }
        catch (LastfmException ex) when (ex.Code == 6) { return null; }
    }

    public async Task<IReadOnlyList<MusicMetadata>> SearchAsync(string kind, string name, string? artist, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || !(await credentials.GetAsync(ct).ConfigureAwait(false)).IsConfigured) return [];
        var parameters = new Dictionary<string, string> { [kind] = name, ["limit"] = "20" };
        if (kind != "artist" && !string.IsNullOrWhiteSpace(artist)) parameters["artist"] = artist;
        using var result = await client.CallAsync(kind + ".search", parameters, null, RequestPriority.Background, ct).ConfigureAwait(false);
        var matches = MusicJson.Child(MusicJson.Child(MusicJson.Child(result.RootElement, "results"), kind + "matches"), kind);
        return MusicJson.Items(matches).Select(node => Parse(kind, node)).Take(20).ToArray();
    }

    public async Task ReserveNativeCopyAsync(string identity, MusicMetadata metadata, CancellationToken ct)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        // Reserve a conservative encoded copy plus per-item database/NFO overhead in the same global budget.
        // This contains no API data and is retained after refresh/deletion so accounting never silently shrinks.
        var size = checked(JsonSerializer.SerializeToUtf8Bytes(metadata).Length * 4 + 2048);
        await store.UpdateAsync<string>(Guid.Empty, "feature-native-" + hash,
            previous => previous is not null && previous.Length >= size ? previous : new string('0', size), ct).ConfigureAwait(false);
    }

    private static Dictionary<string, string>? Parameters(string kind, string name, string? artist, string? mbid)
    {
        if (kind is not ("artist" or "album" or "track")) throw new ArgumentException("Unsupported metadata kind.", nameof(kind));
        if (Guid.TryParse(mbid, out var id)) return new() { ["mbid"] = id.ToString(), ["autocorrect"] = "0" };
        if (string.IsNullOrWhiteSpace(name) || kind != "artist" && string.IsNullOrWhiteSpace(artist)) return null;
        var parameters = new Dictionary<string, string> { [kind] = name, ["autocorrect"] = "0" };
        if (kind != "artist") parameters["artist"] = artist!;
        return parameters;
    }

    private static MusicMetadata Parse(string kind, JsonElement node)
    {
        var description = MusicJson.Child(node, kind == "artist" ? "bio" : "wiki");
        var summary = PlainText(MusicJson.Text(description, "summary"), kind == "artist" ? 300 : 4000);
        var tags = MusicJson.Child(node, "tags");
        if (tags.ValueKind == JsonValueKind.Undefined) tags = MusicJson.Child(node, "toptags");
        return new(PlainText(MusicJson.Text(node, "name"), 500), PlainText(MusicJson.Text(node, "artist"), 500),
            Guid.TryParse(MusicJson.Text(node, "mbid"), out var id) ? id.ToString() : null,
            MusicJson.SafeUrl(MusicJson.Text(node, "url")), summary,
            MusicJson.Items(MusicJson.Child(tags, "tag")).Select(t => PlainText(MusicJson.Text(t, "name"), 80))
                .Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray());
    }

    public static string PlainText(string text, int limit)
    {
        var decoded = Markup().Replace(WebUtility.HtmlDecode(text), string.Empty);
        var clean = string.Concat(decoded.Where(c => !char.IsControl(c) || c == '\n')).Trim();
        return clean.Length <= limit ? clean : clean[..limit];
    }

    [GeneratedRegex("<[^>]*>", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Markup();
}
