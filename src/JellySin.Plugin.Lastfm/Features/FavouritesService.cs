using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Storage;

namespace JellySin.Plugin.Lastfm.Features;

public sealed record FavouriteRemoval(Guid Id, Guid ItemId, MusicTrack Track, string RemoveFrom);
public sealed record FavouriteReview(bool Enabled, IReadOnlyList<FavouriteRemoval> Pending, DateTimeOffset? LastSync,
    string? Status);
public sealed record FavouriteObservation(MusicTrack Track, bool Local, bool Remote);
public sealed record FavouriteState(bool Enabled, Dictionary<Guid, FavouriteObservation> Observed,
    List<FavouriteRemoval> Pending, DateTimeOffset? LastSync = null, string? Status = null);

public sealed class FavouritesService(MusicApi api, IMusicLibrary library, IMusicWriter writer, IStateStore store,
    FeatureLocks locks, TimeProvider clock, AccountService accounts)
{
    private const string Key = "feature-favourites";

    public async Task<FavouriteReview> GetReviewAsync(Guid userId, CancellationToken ct) =>
        Review(await ReadAsync(userId, ct).ConfigureAwait(false));

    public async Task<FavouriteReview> SetEnabledAsync(Guid userId, bool enabled, CancellationToken ct)
    {
        using var accountOperation = accounts.LinkOperation(userId, ct);
        ct = accountOperation.Token;
        using (await locks.EnterAsync(userId, ct).ConfigureAwait(false))
        {
            var state = await ReadAsync(userId, ct).ConfigureAwait(false);
            // A fresh opt-in starts an additive merge. Old differences must not become removal instructions.
            state = enabled && !state.Enabled ? new(true, [], []) : state with { Enabled = enabled };
            await store.WriteAsync(userId, Key, state, ct).ConfigureAwait(false);
        }
        return enabled ? await SyncAsync(userId, ct).ConfigureAwait(false) : await GetReviewAsync(userId, ct).ConfigureAwait(false);
    }

    public async Task<FavouriteReview> SyncAsync(Guid userId, CancellationToken ct)
    {
        using var accountOperation = accounts.LinkOperation(userId, ct);
        ct = accountOperation.Token;
        using var lease = await locks.EnterAsync(userId, ct).ConfigureAwait(false);
        var state = await ReadAsync(userId, ct).ConfigureAwait(false);
        if (!state.Enabled) return Review(state);
        var remote = await api.GetAllLovedAsync(userId, ct).ConfigureAwait(false);
        if (!remote.Complete)
        {
            state = state with { Status = "Last.fm returned an incomplete or changing loved-track collection; synchronization is paused." };
            await store.WriteAsync(userId, Key, state, ct).ConfigureAwait(false);
            return Review(state);
        }
        var remoteItems = Resolve(userId, remote.Tracks, ct);
        var remoteNames = remote.Tracks.Select(MusicFeatureService.TrackKey).ToHashSet(StringComparer.Ordinal);
        var localItems = library.GetFavourites(userId, ct).ToDictionary(t => t.Id);
        var ids = localItems.Keys.Concat(remoteItems.Keys).Concat(state.Observed.Keys).Distinct().Take(20001).ToArray();
        if (ids.Length > 20000) throw new InvalidOperationException("Favourite reconciliation reached its 20,000-track limit.");
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            LocalMusic local;
            try { local = library.Get(userId, id, ct); }
            catch (KeyNotFoundException) { state.Observed.Remove(id); state.Pending.RemoveAll(p => p.ItemId == id); continue; }
            if (string.IsNullOrWhiteSpace(local.Track.Artist) || string.IsNullOrWhiteSpace(local.Track.Title))
            {
                state.Observed.Remove(id);
                state.Pending.RemoveAll(p => p.ItemId == id);
                continue;
            }
            // An ambiguous remote match cannot prove a removal of an already observed local track.
            var remotelyLoved = remoteItems.ContainsKey(id) || remoteNames.Contains(MusicFeatureService.TrackKey(local.Track));
            await ReconcileAsync(userId, local, remotelyLoved, state, ct).ConfigureAwait(false);
        }
        state = state with { LastSync = clock.GetUtcNow(), Status = "Synchronized; removals require review." };
        await store.WriteAsync(userId, Key, state, ct).ConfigureAwait(false);
        return Review(state);
    }

    public async Task<FavouriteReview> ReviewRemovalAsync(Guid userId, Guid reviewId, bool apply, CancellationToken ct)
    {
        using var accountOperation = accounts.LinkOperation(userId, ct);
        ct = accountOperation.Token;
        using var lease = await locks.EnterAsync(userId, ct).ConfigureAwait(false);
        var state = await ReadAsync(userId, ct).ConfigureAwait(false);
        if (!state.Enabled) throw new InvalidOperationException("Favourite synchronization is disabled.");
        var pending = state.Pending.SingleOrDefault(p => p.Id == reviewId) ?? throw new KeyNotFoundException("Review no longer exists.");
        var remote = await api.GetAllLovedAsync(userId, ct).ConfigureAwait(false);
        if (!remote.Complete) throw new InvalidOperationException("Cannot verify this removal while Last.fm's collection is incomplete.");
        var remotelyLoved = remote.Tracks.Any(t => MusicFeatureService.TrackKey(t) == MusicFeatureService.TrackKey(pending.Track));
        var local = library.Get(userId, pending.ItemId, ct);
        if (library.Match(userId, pending.Track, ct).ItemId != pending.ItemId)
            throw new InvalidOperationException("The track's identity changed. Synchronize again.");
        var unchanged = pending.RemoveFrom == "lastfm" ? !local.Favourite && remotelyLoved : local.Favourite && !remotelyLoved;
        if (apply && !unchanged) throw new InvalidOperationException("This favourite changed after the review was created. Synchronize again.");
        if (apply && pending.RemoveFrom == "lastfm")
            await api.SetLovedAsync(userId, pending.Track, false, ct).ConfigureAwait(false);
        else if (apply) writer.SetFavourite(userId, pending.ItemId, false, ct);
        state.Pending.Remove(pending);
        state.Observed[pending.ItemId] = new(local.Track, apply ? false : local.Favourite, apply ? false : remotelyLoved);
        await store.WriteAsync(userId, Key, state, ct).ConfigureAwait(false);
        return Review(state);
    }

    private async Task ReconcileAsync(Guid userId, LocalMusic local, bool remote, FavouriteState state, CancellationToken ct)
    {
        var previous = state.Observed.GetValueOrDefault(local.Id);
        if (previous is not null && MusicFeatureService.TrackKey(previous.Track) != MusicFeatureService.TrackKey(local.Track))
        {
            state.Pending.RemoveAll(p => p.ItemId == local.Id);
            previous = null;
        }
        var pending = state.Pending.FirstOrDefault(p => p.ItemId == local.Id);
        if (pending is not null && local.Favourite == remote)
        {
            state.Pending.Remove(pending);
            pending = null;
        }
        if (pending is null && previous is not null && local.Favourite != remote)
        {
            if (previous.Local && !local.Favourite && remote)
                state.Pending.Add(new(Guid.NewGuid(), local.Id, local.Track, "lastfm"));
            else if (previous.Remote && !remote && local.Favourite)
                state.Pending.Add(new(Guid.NewGuid(), local.Id, local.Track, "jellyfin"));
        }
        if (!state.Pending.Any(p => p.ItemId == local.Id))
        {
            if (local.Favourite && !remote && (previous is null || !previous.Local))
            {
                await api.SetLovedAsync(userId, local.Track, true, ct).ConfigureAwait(false);
                remote = true;
            }
            else if (!local.Favourite && remote && (previous is null || !previous.Remote))
            {
                // Read again after remote work. Do not overwrite a newer local edit.
                var current = library.Get(userId, local.Id, ct);
                if (current.Favourite == local.Favourite) writer.SetFavourite(userId, local.Id, true, ct);
                local = library.Get(userId, local.Id, ct);
            }
        }
        state.Observed[local.Id] = new(local.Track, local.Favourite, remote);
    }

    private Dictionary<Guid, MusicTrack> Resolve(Guid userId, IReadOnlyList<MusicTrack> tracks, CancellationToken ct)
    {
        var result = new Dictionary<Guid, MusicTrack>();
        foreach (var track in tracks)
            if (library.Match(userId, track, ct).ItemId is { } id) result.TryAdd(id, track);
        return result;
    }

    private async Task<FavouriteState> ReadAsync(Guid id, CancellationToken ct) =>
        await store.ReadAsync<FavouriteState>(id, Key, ct).ConfigureAwait(false) ?? new(false, [], []);

    private static FavouriteReview Review(FavouriteState state) => new(state.Enabled, state.Pending, state.LastSync, state.Status);
}
