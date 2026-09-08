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

    public async Task<int> ApplyHistoryImportAsync(Guid userId, Guid previewId, CancellationToken ct)
    {
        using var operation = accounts.LinkOperation(userId, ct);
        ct = operation.Token;
        using var lease = await locks.EnterAsync(userId, ct).ConfigureAwait(false);
        var preview = await LoadPreviewAsync(userId, previewId, ct).ConfigureAwait(false);
        foreach (var entry in preview.Entries)
        {
            var current = library.Match(userId, entry.Track, ct);
            if (current.ItemId != entry.ItemId) throw new InvalidOperationException("The library changed. Create a new preview.");
            writer.ApplyHistoryFloor(userId, entry.ItemId, entry.ProposedPlayCount, entry.ProposedLastPlayed, ct);
        }
        await store.DeleteAsync(userId, PreviewKey, ct).ConfigureAwait(false);
        return preview.Entries.Count;
    }

    private async Task<HistoryImportPreview> ExtendPreviewAsync(Guid userId, HistoryImportPreview preview, CancellationToken ct)
    {
        var recent = await api.GetPageAsync(userId, "user.getRecentTracks", 1, null, ct, preview.Until).ConfigureAwait(false);
        var dates = recent.Tracks.Where(t => !t.NowPlaying && t.PlayedAt <= clock.GetUtcNow())
            .GroupBy(TrackKey).ToDictionary(g => g.Key, g => g.Max(t => t.PlayedAt)?.UtcDateTime, StringComparer.Ordinal);
        var entries = preview.Entries.ToDictionary(e => e.ItemId);
        var unmatched = preview.Unmatched.ToList();
        var finalPage = Math.Min(500, preview.NextPage + 9);
        if (preview.NextPage > 500) throw new InvalidOperationException("The 100,000-track import limit was reached. The saved preview can still be reviewed and applied.");
        for (var page = preview.NextPage; page <= finalPage; page++)
        {
            var result = await api.GetPageAsync(userId, "user.getTopTracks", page, "overall", ct).ConfigureAwait(false);
            AddEntries(userId, result.Tracks, dates, entries, unmatched, ct);
            preview = preview with
            {
                Entries = entries.Values.ToArray(),
                Unmatched = unmatched.ToArray(),
                Complete = result.Complete,
                NextPage = page + 1,
                ExpiresAt = clock.GetUtcNow().AddMinutes(30)
            };
            await store.WriteAsync(userId, PreviewKey, preview, ct).ConfigureAwait(false);
            if (result.Complete || result.Tracks.Count == 0) break;
        }
        return preview;
    }

    private void AddEntries(Guid userId, IReadOnlyList<MusicTrack> tracks, Dictionary<string, DateTime?> dates,
        Dictionary<Guid, HistoryImportEntry> entries, List<MusicMatch> unmatched, CancellationToken ct)
    {
        foreach (var track in tracks)
        {
            var match = library.Match(userId, track, ct);
            if (match.ItemId is not { } id) { unmatched.Add(match); continue; }
            var current = library.Get(userId, id, ct);
            var previous = entries.GetValueOrDefault(id);
            var count = Math.Max(Math.Max(current.PlayCount, track.PlayCount), previous?.ProposedPlayCount ?? 0);
            var date = Later(Later(current.LastPlayed, dates.GetValueOrDefault(TrackKey(track))), previous?.ProposedLastPlayed);
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
