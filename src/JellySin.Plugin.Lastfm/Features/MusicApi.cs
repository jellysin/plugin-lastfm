using System.Globalization;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Transport;

namespace JellySin.Plugin.Lastfm.Features;

public sealed class MusicApi(ILastfmClient client, AccountService accounts, TimeProvider clock, MusicViewCache? cache = null)
{
    public static IReadOnlyList<string> Periods { get; } = ["overall", "7day", "1month", "3month", "6month", "12month"];

    public async Task<MusicPage> GetPageAsync(Guid userId, string method, int page, string? period, CancellationToken ct, long? until = null, bool fresh = false)
    {
        using var operation = accounts.LinkOperation(userId, ct);
        ct = operation.Token;
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(page, method == "user.getRecentTracks" ? 100_000 : 500);
        if (method is not ("user.getRecentTracks" or "user.getLovedTracks" or "user.getTopTracks"))
            throw new ArgumentException("Unsupported track collection.", nameof(method));
        if (period is not null && !Periods.Contains(period, StringComparer.Ordinal))
            throw new ArgumentException("Unsupported chart period.", nameof(period));
        var account = await accounts.GetAsync(userId, ct).ConfigureAwait(false)
            ?? throw new NotConnectedException();
        var cacheKey = string.Join('|', account.Username, method, page.ToString(CultureInfo.InvariantCulture), period, until?.ToString(CultureInfo.InvariantCulture));
        if (!fresh && cache is not null && await cache.ReadAsync<MusicPage>(userId, cacheKey, ct).ConfigureAwait(false) is { } cached) return cached;
        var parameters = new Dictionary<string, string>
        {
            ["user"] = account.Username,
            ["page"] = page.ToString(CultureInfo.InvariantCulture),
            ["limit"] = "200",
        };
        if (period is not null) parameters["period"] = period;
        if (method == "user.getRecentTracks")
        {
            until ??= clock.GetUtcNow().ToUnixTimeSeconds();
            if (until < 0 || until > clock.GetUtcNow().ToUnixTimeSeconds()) throw new ArgumentException("Invalid history snapshot.", nameof(until));
            parameters["to"] = until.Value.ToString(CultureInfo.InvariantCulture);
        }
        using var document = await client.CallAsync(method, parameters, null, RequestPriority.Interactive, ct).ConfigureAwait(false);
        var rootName = method switch { "user.getLovedTracks" => "lovedtracks", "user.getTopTracks" => "toptracks", _ => "recenttracks" };
        var root = MusicJson.Child(document.RootElement, rootName);
        if (root.ValueKind != System.Text.Json.JsonValueKind.Object) throw new InvalidDataException("Missing Last.fm collection.");
        var attributes = MusicJson.Child(root, "@attr");
        var total = MusicJson.Number(attributes, "totalPages");
        var tracks = MusicJson.Items(MusicJson.Child(root, "track")).Select(MusicJson.Track)
            .Where(t => !string.IsNullOrWhiteSpace(t.Artist) && !string.IsNullOrWhiteSpace(t.Title)).ToArray();
        var actualPage = MusicJson.Number(attributes, "page");
        var valid = attributes.ValueKind == System.Text.Json.JsonValueKind.Object && attributes.TryGetProperty("totalPages", out _)
            && actualPage == page && (total >= page || total == 0 && tracks.Length == 0);
        if (!valid) throw new InvalidDataException("Invalid Last.fm pagination metadata.");
        var result = new MusicPage(tracks, page, total, page >= total, until, MusicJson.Number(attributes, "total"));
        if (!fresh && cache is not null) await cache.WriteAsync(userId, cacheKey, result, client.GetCacheLifetime(document), ct).ConfigureAwait(false);
        return result;
    }

    public async Task<MusicPage> GetAllLovedAsync(Guid userId, CancellationToken ct)
    {
        var tracks = new List<MusicTrack>();
        var expectedPages = -1;
        MusicPage? first = null;
        MusicPage? last = null;
        for (var page = 1; page <= 50; page++)
        {
            var result = await GetPageAsync(userId, "user.getLovedTracks", page, null, ct, fresh: true).ConfigureAwait(false);
            first ??= result;
            last = result;
            if (expectedPages >= 0 && expectedPages != result.TotalPages) return Unfinished(LovedReadCheck.PageCountChanged, tracks, page, first, result);
            if (result.TotalTracks != first.TotalTracks) return Unfinished(LovedReadCheck.TotalChanged, tracks, page, first, result);
            expectedPages = result.TotalPages;
            tracks.AddRange(result.Tracks);
            if (result.Complete)
            {
                var check = await GetPageAsync(userId, "user.getLovedTracks", 1, null, ct, fresh: true).ConfigureAwait(false);
                return Unstable(first, check, tracks) is { } failed
                    ? Unfinished(failed, tracks, page, first, check, failed == LovedReadCheck.FirstPageChanged ? Compare(first, check) : null)
                    : new(tracks, page, result.TotalPages, true);
            }
            if (result.Tracks.Count == 0) return Unfinished(LovedReadCheck.EmptyPage, tracks, page, first, result);
        }
        return Unfinished(LovedReadCheck.PageLimit, tracks, 50, first!, last!);
    }

    /// <summary>The first check a completed read of the loved tracks fails, or none when the collection held still.</summary>
    private static LovedReadCheck? Unstable(MusicPage first, MusicPage check, List<MusicTrack> tracks)
    {
        if (check.TotalTracks != first.TotalTracks) return LovedReadCheck.TotalChanged;
        if (tracks.Count != first.TotalTracks) return LovedReadCheck.ItemCountDiffers;
        if (!check.Tracks.Select(LovedIdentity).SequenceEqual(first.Tracks.Select(LovedIdentity))) return LovedReadCheck.FirstPageChanged;
        if (DistinctTracks(tracks) != tracks.Count) return LovedReadCheck.DuplicateTracks;
        return null;
    }

    private static MusicPage Unfinished(LovedReadCheck check, List<MusicTrack> tracks, int page, MusicPage first, MusicPage latest,
        FirstPageDifference? firstPage = null) =>
        new(tracks, page, latest.TotalPages, false,
            Gap: new(check, page, latest.TotalPages, first.TotalTracks, latest.TotalTracks, tracks.Count, DistinctTracks(tracks), firstPage));

    /// <summary>Whether page 1 moved its tracks or changed them, and how many of them share a loved date, which leaves their order open.</summary>
    private static FirstPageDifference Compare(MusicPage first, MusicPage again)
    {
        var before = first.Tracks.Select(LovedIdentity).ToArray();
        var after = again.Tracks.Select(LovedIdentity).ToArray();
        var positions = Math.Max(before.Length, after.Length);
        var differing = Enumerable.Range(0, positions).Count(i => i >= before.Length || i >= after.Length || before[i] != after[i]);
        var counts = after.CountBy(identity => identity).ToDictionary();
        var sameTracks = before.Length == after.Length && before.CountBy(identity => identity).All(group => counts.GetValueOrDefault(group.Key) == group.Value);
        var tiedDates = first.Tracks.Select(t => t.PlayedAt).OfType<DateTimeOffset>().CountBy(date => date).Where(group => group.Value > 1).Sum(group => group.Value);
        return new(positions, differing, sameTracks, tiedDates);
    }

    private static int DistinctTracks(List<MusicTrack> tracks) =>
        tracks.Select(MusicFeatureService.TrackKey).Distinct(StringComparer.Ordinal).Count();

    private static (string Name, string? Id, DateTimeOffset? Date) LovedIdentity(MusicTrack track) =>
        (MusicFeatureService.TrackKey(track), track.MusicBrainzId, track.PlayedAt);

    public async Task<IReadOnlyList<MusicTrack>> SimilarAsync(MusicTrack seed, CancellationToken ct)
    {
        using var response = await GetDiscoveryAsync("track.getSimilar", new Dictionary<string, string>
        { ["artist"] = seed.Artist, ["track"] = seed.Title, ["limit"] = "100", ["autocorrect"] = "0" },
            ct).ConfigureAwait(false);
        if (response is null) return [];
        return MusicJson.Items(MusicJson.Child(MusicJson.Child(response.RootElement, "similartracks"), "track"))
            .Select(MusicJson.Track).Where(t => t.Artist.Length > 0 && t.Title.Length > 0).Take(100).ToArray();
    }

    public async Task SetLovedAsync(Guid userId, MusicTrack track, bool loved, CancellationToken ct)
    {
        using var operation = accounts.LinkOperation(userId, ct);
        ct = operation.Token;
        var account = await accounts.GetAsync(userId, ct).ConfigureAwait(false)
            ?? throw new NotConnectedException();
        if (account.NeedsReconnect) throw new InvalidOperationException("Reconnect the Last.fm account before changing favourites.");
        try
        {
            using var result = await client.CallForApplicationAsync(loved ? "track.love" : "track.unlove",
                new Dictionary<string, string> { ["artist"] = track.Artist, ["track"] = track.Title },
                account.SessionKey, account.ApplicationIdentity, RequestPriority.Background, ct).ConfigureAwait(false);
        }
        catch (LastfmException ex) when (ex.Code == 9)
        {
            await accounts.MarkReconnectAsync(userId, ct).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<MusicOverview> GetOverviewAsync(Guid userId, string period, CancellationToken ct)
    {
        using var operation = accounts.LinkOperation(userId, ct);
        ct = operation.Token;
        if (!Periods.Contains(period, StringComparer.Ordinal)) throw new ArgumentException("Unsupported chart period.", nameof(period));
        var account = await accounts.GetAsync(userId, ct).ConfigureAwait(false) ?? throw new NotConnectedException();
        var tracks = await GetPageAsync(userId, "user.getTopTracks", 1, period, ct).ConfigureAwait(false);
        var artists = await GetRankedAsync(userId, account.Username, "artist", period, ct).ConfigureAwait(false);
        var albums = await GetRankedAsync(userId, account.Username, "album", period, ct).ConfigureAwait(false);
        return new(period, artists, albums, tracks.Tracks, await GetStatisticsAsync(userId, account.Username, ct).ConfigureAwait(false));
    }

    private async Task<ListeningStatistics> GetStatisticsAsync(Guid userId, string username, CancellationToken ct)
    {
        var cacheKey = "profile|" + username;
        if (cache is not null && await cache.ReadAsync<ListeningStatistics>(userId, cacheKey, ct).ConfigureAwait(false) is { } cached) return cached;
        using var response = await client.CallAsync("user.getInfo", new Dictionary<string, string> { ["user"] = username },
            null, RequestPriority.Interactive, ct).ConfigureAwait(false);
        var profile = MusicJson.Child(response.RootElement, "user");
        var result = new ListeningStatistics(username, MusicJson.Number(profile, "playcount"),
            MusicJson.Number(profile, "artist_count"), MusicJson.Number(profile, "album_count"), MusicJson.Number(profile, "track_count"),
            MusicJson.SafeUrl(MusicJson.Text(profile, "url")));
        if (cache is not null) await cache.WriteAsync(userId, cacheKey, result, client.GetCacheLifetime(response), ct).ConfigureAwait(false);
        return result;
    }

    private async Task<IReadOnlyList<RankedMusic>> GetRankedAsync(Guid userId, string username, string kind, string period, CancellationToken ct)
    {
        var cacheKey = string.Join('|', username, kind, period);
        if (cache is not null && await cache.ReadAsync<RankedMusic[]>(userId, cacheKey, ct).ConfigureAwait(false) is { } cached) return cached;
        using var response = await client.CallAsync(kind == "artist" ? "user.getTopArtists" : "user.getTopAlbums",
            new Dictionary<string, string> { ["user"] = username, ["period"] = period, ["limit"] = "50" },
            null, RequestPriority.Interactive, ct).ConfigureAwait(false);
        var result = MusicJson.Items(MusicJson.Child(MusicJson.Child(response.RootElement, "top" + kind + "s"), kind)).Select(node =>
            new RankedMusic(MusicJson.Text(node, "name"), kind == "album" ? MusicJson.Text(node, "artist") : null,
                MusicJson.SafeUrl(MusicJson.Text(node, "url")), Guid.TryParse(MusicJson.Text(node, "mbid"), out var id) ? id.ToString() : null,
                MusicJson.Number(node, "playcount"))).Take(50).ToArray();
        if (cache is not null) await cache.WriteAsync(userId, cacheKey, result, client.GetCacheLifetime(response), ct).ConfigureAwait(false);
        return result;
    }

    public async Task<IReadOnlyList<MusicTrack>> ArtistTracksAsync(string artist, CancellationToken ct)
    {
        using var response = await GetDiscoveryAsync("artist.getTopTracks", new Dictionary<string, string>
        { ["artist"] = artist, ["limit"] = "5", ["autocorrect"] = "0" }, ct).ConfigureAwait(false);
        if (response is null) return [];
        return MusicJson.Items(MusicJson.Child(MusicJson.Child(response.RootElement, "toptracks"), "track")).Select(MusicJson.Track).Take(5).ToArray();
    }

    public async Task<IReadOnlyList<DiscoveryEntity>> ArtistDiscoveryAsync(string artist, bool albums, CancellationToken ct)
    {
        using var response = await GetDiscoveryAsync(albums ? "artist.getTopAlbums" : "artist.getSimilar", new Dictionary<string, string>
        { ["artist"] = artist, ["limit"] = "10", ["autocorrect"] = "0" }, ct).ConfigureAwait(false);
        if (response is null) return [];
        var kind = albums ? "album" : "artist";
        var root = MusicJson.Child(MusicJson.Child(response.RootElement, albums ? "topalbums" : "similarartists"), kind);
        return MusicJson.Items(root).Select(node => new DiscoveryEntity(kind, MusicJson.Text(node, "name"), albums ? artist : null,
            MusicJson.SafeUrl(MusicJson.Text(node, "url")), Guid.TryParse(MusicJson.Text(node, "mbid"), out var id) ? id.ToString() : null)).Take(10).ToArray();
    }

    private async Task<System.Text.Json.JsonDocument?> GetDiscoveryAsync(string method, Dictionary<string, string> parameters, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parameters["artist"]);
        if (parameters.TryGetValue("track", out var track)) ArgumentException.ThrowIfNullOrWhiteSpace(track);
        try
        {
            return await client.CallAsync(method, parameters, null, RequestPriority.Background, ct).ConfigureAwait(false);
        }
        // Last.fm also uses error 6 for unknown tracks/artists, even when all required parameters are present.
        catch (LastfmException error) when (error.Code is 6 or 7) { return null; }
    }
}
