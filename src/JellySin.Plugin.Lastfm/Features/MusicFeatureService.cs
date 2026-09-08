using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Storage;

namespace JellySin.Plugin.Lastfm.Features;

public sealed class MusicFeatureService(MusicApi api, IMusicLibrary library, IMusicWriter writer, IStateStore store,
    FeatureLocks locks, TimeProvider clock, AccountService accounts)
{
    private const string PreviewKey = "feature-history-preview";

    public Task<MusicPage> GetHistoryAsync(Guid userId, int page, CancellationToken ct, long? until = null) =>
        api.GetPageAsync(userId, "user.getRecentTracks", page, null, ct, until);

    public Task<MusicOverview> GetOverviewAsync(Guid userId, string period, CancellationToken ct) => api.GetOverviewAsync(userId, period, ct);

    public async Task<HistoryImportPreview?> GetHistoryImportPreviewAsync(Guid userId, CancellationToken ct)
    {
        var preview = await store.ReadAsync<HistoryImportPreview>(userId, PreviewKey, ct).ConfigureAwait(false);
        return preview?.ExpiresAt > clock.GetUtcNow() ? preview : null;
    }

    public async Task<MusicChart> GetChartsAsync(Guid userId, string period, CancellationToken ct)
    {
        var page = await api.GetPageAsync(userId, "user.getTopTracks", 1, period, ct).ConfigureAwait(false);
        return new(period, page.Tracks);
    }

    public async Task<HistoryImportPreview> PreviewHistoryImportAsync(Guid userId, CancellationToken ct)
    {
        using var operation = accounts.LinkOperation(userId, ct);
        ct = operation.Token;
        using var lease = await locks.EnterAsync(userId, ct).ConfigureAwait(false);
        var preview = new HistoryImportPreview(Guid.NewGuid(), clock.GetUtcNow().AddMinutes(30), [], [], false, 1, clock.GetUtcNow().ToUnixTimeSeconds());
        return await ExtendPreviewAsync(userId, preview, ct).ConfigureAwait(false);
    }

    public async Task<HistoryImportPreview> ContinueHistoryImportAsync(Guid userId, Guid previewId, CancellationToken ct)
    {
        using var operation = accounts.LinkOperation(userId, ct);
        ct = operation.Token;
        using var lease = await locks.EnterAsync(userId, ct).ConfigureAwait(false);
        var preview = await LoadPreviewAsync(userId, previewId, ct).ConfigureAwait(false);
        return preview.Complete ? preview : await ExtendPreviewAsync(userId, preview, ct).ConfigureAwait(false);
    }

    public async Task<HistoryImportResult> ApplyHistoryImportAsync(Guid userId, Guid previewId, CancellationToken ct)
    {
        using var operation = accounts.LinkOperation(userId, ct);
        ct = operation.Token;
        using var lease = await locks.EnterAsync(userId, ct).ConfigureAwait(false);
        var preview = await LoadPreviewAsync(userId, previewId, ct).ConfigureAwait(false);
        if (!preview.Complete) throw new InvalidOperationException("Finish retrieving counts and last-played dates before applying this preview.");
        var chunk = preview.Entries.Skip(preview.AppliedCount).Take(200).ToArray();
        foreach (var entry in chunk)
        {
            var current = library.Match(userId, entry.Track, ct);
            if (current.ItemId != entry.ItemId) throw new InvalidOperationException("The library changed. Create a new preview.");
            writer.ApplyHistoryFloor(userId, entry.ItemId, entry.ProposedPlayCount, entry.ProposedLastPlayed, ct);
        }
        preview = preview with { AppliedCount = preview.AppliedCount + chunk.Length, ExpiresAt = clock.GetUtcNow().AddMinutes(30) };
        if (preview.AppliedCount == preview.Entries.Count) await store.DeleteAsync(userId, PreviewKey, ct).ConfigureAwait(false);
        else await store.WriteAsync(userId, PreviewKey, preview, ct).ConfigureAwait(false);
        return new(preview.AppliedCount, preview.Entries.Count, preview.AppliedCount == preview.Entries.Count);
    }

    private async Task<HistoryImportPreview> ExtendPreviewAsync(Guid userId, HistoryImportPreview preview, CancellationToken ct)
    {
        var entries = preview.Entries.ToDictionary(e => e.ItemId);
        var unmatched = preview.Unmatched.ToList();
        var pages = 0;
        while (!preview.CountsComplete && pages < 10)
        {
            pages++;
            if (preview.NextPage > 500) throw new InvalidOperationException("The 100,000-track import limit was reached; this account's full history exceeds the supported import size.");
            var result = await api.GetPageAsync(userId, "user.getTopTracks", preview.NextPage, "overall", ct, fresh: true).ConfigureAwait(false);
            if (!result.Complete && result.Tracks.Count == 0) throw new InvalidDataException("Last.fm returned an incomplete count collection; resume the saved preview.");
            AddEntries(userId, result.Tracks, entries, unmatched, ct);
            preview = preview with
            {
                Entries = entries.Values.ToArray(),
                Unmatched = unmatched.ToArray(),
                CountsComplete = result.Complete,
                NextPage = preview.NextPage + 1,
                ExpiresAt = clock.GetUtcNow().AddMinutes(30)
            };
            await store.WriteAsync(userId, PreviewKey, preview, ct).ConfigureAwait(false);
        }
        if (preview.CountsComplete) preview = await ExtendDatesAsync(userId, preview, Math.Max(0, 10 - pages), ct).ConfigureAwait(false);
        return preview;
    }

    private async Task<HistoryImportPreview> ExtendDatesAsync(Guid userId, HistoryImportPreview preview, int pageBudget, CancellationToken ct)
    {
        var entries = preview.Entries.ToDictionary(entry => entry.ItemId);
        var resolved = (preview.DatedItemIds ?? []).ToHashSet();
        for (var count = 0; count < pageBudget && !preview.DatesComplete; count++)
        {
            if (resolved.Count == entries.Count) { preview = preview with { DatesComplete = true }; break; }
            if (preview.RecentNextPage > 100_000) throw new InvalidOperationException("The 20-million-listen retrieval limit was reached; this account exceeds the supported import size.");
            var remaining = entries.Values.Where(entry => !resolved.Contains(entry.ItemId)).ToArray();
            var keys = remaining.Select(entry => TrackKey(entry.Track)).ToHashSet(StringComparer.Ordinal);
            var identifiers = remaining.Select(entry => entry.Track.MusicBrainzId).Where(id => id is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var recent = await api.GetPageAsync(userId, "user.getRecentTracks", preview.RecentNextPage, null, ct, preview.Until, fresh: true).ConfigureAwait(false);
            if (!recent.Complete && recent.Tracks.Count == 0) throw new InvalidDataException("Last.fm returned an incomplete listening history; resume the saved preview.");
            foreach (var track in recent.Tracks.Where(track => !track.NowPlaying && track.PlayedAt.HasValue
                         && track.PlayedAt.Value.ToUnixTimeSeconds() <= preview.Until
                         && (keys.Contains(TrackKey(track)) || track.MusicBrainzId is { } mbid && identifiers.Contains(mbid))))
            {
                var match = library.Match(userId, track, ct);
                if (match.ItemId is not { } id || !entries.TryGetValue(id, out var entry)) continue;
                entries[id] = entry with { ProposedLastPlayed = Later(entry.ProposedLastPlayed, track.PlayedAt!.Value.UtcDateTime) };
                resolved.Add(id);
            }
            preview = preview with
            {
                Entries = entries.Values.ToArray(),
                DatedItemIds = resolved.ToArray(),
                RecentNextPage = preview.RecentNextPage + 1,
                DatesComplete = recent.Complete || resolved.Count == entries.Count,
                ExpiresAt = clock.GetUtcNow().AddMinutes(30)
            };
            await store.WriteAsync(userId, PreviewKey, preview, ct).ConfigureAwait(false);
        }
        preview = preview with { Complete = preview.CountsComplete && preview.DatesComplete };
        await store.WriteAsync(userId, PreviewKey, preview, ct).ConfigureAwait(false);
        return preview;
    }

    private void AddEntries(Guid userId, IReadOnlyList<MusicTrack> tracks,
        Dictionary<Guid, HistoryImportEntry> entries, List<MusicMatch> unmatched, CancellationToken ct)
    {
        foreach (var track in tracks)
        {
            var match = library.Match(userId, track, ct);
            if (match.ItemId is not { } id) { unmatched.Add(match); continue; }
            var current = library.Get(userId, id, ct);
            var previous = entries.GetValueOrDefault(id);
            var count = Math.Max(Math.Max(current.PlayCount, track.PlayCount), previous?.ProposedPlayCount ?? 0);
            var date = Later(current.LastPlayed, previous?.ProposedLastPlayed);
            entries[id] = new(id, track, current.PlayCount, count, current.LastPlayed, date);
        }
    }

    private async Task<HistoryImportPreview> LoadPreviewAsync(Guid userId, Guid id, CancellationToken ct)
    {
        var preview = await store.ReadAsync<HistoryImportPreview>(userId, PreviewKey, ct).ConfigureAwait(false);
        if (preview is null || preview.Id != id || preview.ExpiresAt <= clock.GetUtcNow())
            throw new InvalidOperationException("Create a fresh history preview before importing.");
        return preview;
    }

    internal static string TrackKey(MusicTrack track) => string.Join('\u001f', track.Artist.Trim().ToUpperInvariant(), track.Title.Trim().ToUpperInvariant());
    private static DateTime? Later(DateTime? left, DateTime? right) => !left.HasValue || right > left ? right : left;
}
