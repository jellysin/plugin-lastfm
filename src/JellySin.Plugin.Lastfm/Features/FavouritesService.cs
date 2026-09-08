using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Storage;

namespace JellySin.Plugin.Lastfm.Features;

public sealed record FavouriteRemoval(Guid Id, Guid ItemId, MusicTrack Track, string RemoveFrom,
    string LocalRevision = "", DateTimeOffset? RemoteLovedAt = null);
public sealed record FavouriteReview(bool Enabled, IReadOnlyList<FavouriteRemoval> Pending, DateTimeOffset? LastSync,
    string? Status);
public sealed record FavouriteObservation(MusicTrack Track, bool Local, bool Remote,
    string LocalRevision = "", DateTimeOffset? RemoteLovedAt = null);
public sealed record FavouriteAddition(Guid ItemId, MusicTrack Track, string Target, string LocalRevision);
public sealed record FavouriteState(bool Enabled, Dictionary<Guid, FavouriteObservation> Observed,
    List<FavouriteRemoval> Pending, DateTimeOffset? LastSync = null, string? Status = null, Guid? AfterItem = null);

public sealed class FavouritesService(MusicApi api, IMusicLibrary library, IMusicWriter writer, IStateStore store,
    FeatureLocks locks, TimeProvider clock, AccountService accounts)
{
    private const string Key = "feature-favourites";
    private const string AdditionsKey = "feature-favourite-additions";

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
        var (remoteItems, unresolved) = Resolve(userId, remote.Tracks, ct);
        await RecoverAdditionsAsync(userId, state, remoteItems, ct).ConfigureAwait(false);
        var additions = new List<FavouriteAddition>();
        var localItems = library.GetFavourites(userId, ct).ToDictionary(t => t.Id);
        var ids = localItems.Keys.Concat(remoteItems.Keys).Concat(state.Observed.Keys).Distinct().Take(20001).ToArray();
        if (ids.Length > 20000) throw new InvalidOperationException("Favourite reconciliation reached its 20,000-track limit.");
        var batch = ids.Order().Where(id => state.AfterItem is null || id.CompareTo(state.AfterItem.Value) > 0).Take(201).ToArray();
        foreach (var id in batch.Take(200))
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
            // A name collision rejected by the library matcher proves neither a love nor a removal.
            if (unresolved.Contains(MusicFeatureService.TrackKey(local.Track))) continue;
            await ReconcileAsync(userId, local, remoteItems.GetValueOrDefault(id), state, additions, ct).ConfigureAwait(false);
        }
        var complete = batch.Length <= 200;
        state = state with
        {
            AfterItem = complete ? null : batch[199],
            LastSync = complete ? clock.GetUtcNow() : state.LastSync,
            Status = !complete ? "Processed 200 tracks. Synchronize again to continue; background refresh also resumes this work."
                : unresolved.Count == 0 ? "Synchronized; removals require review."
                : "Synchronized unambiguous tracks; unresolved identities were left unchanged."
        };
        await store.WriteAsync(userId, Key, state, ct).ConfigureAwait(false);
        await store.DeleteAsync(userId, AdditionsKey, ct).ConfigureAwait(false);
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
        var (remoteItems, unresolved) = Resolve(userId, remote.Tracks, ct);
        if (unresolved.Contains(MusicFeatureService.TrackKey(pending.Track)))
            throw new InvalidOperationException("The remote track cannot be matched unambiguously. Synchronize again.");
        var remoteTrack = remoteItems.GetValueOrDefault(pending.ItemId);
        var remotelyLoved = remoteTrack is not null;
        var local = library.Get(userId, pending.ItemId, ct);
        if (library.Match(userId, pending.Track, ct).ItemId != pending.ItemId)
            throw new InvalidOperationException("The track's identity changed. Synchronize again.");
        var unchanged = pending.RemoveFrom == "lastfm" ? !local.Favourite && remotelyLoved : local.Favourite && !remotelyLoved;
        unchanged &= pending.LocalRevision == local.FavouriteRevision;
        if (pending.RemoveFrom == "lastfm") unchanged &= pending.RemoteLovedAt.HasValue && pending.RemoteLovedAt == remoteTrack?.PlayedAt;
        if (apply && !unchanged) throw new InvalidOperationException("This favourite changed or its edit date cannot be verified. Synchronize again.");
        if (apply && pending.RemoveFrom == "lastfm")
            await api.SetLovedAsync(userId, pending.Track, false, ct).ConfigureAwait(false);
        else if (apply && !writer.TrySetFavourite(userId, pending.ItemId, false, pending.LocalRevision, ct))
            throw new InvalidOperationException("This favourite changed during review. Synchronize again.");
        local = library.Get(userId, pending.ItemId, ct);
        state.Pending.Remove(pending);
        state.Observed[pending.ItemId] = new(local.Track, local.Favourite, apply ? false : remotelyLoved,
            local.FavouriteRevision, apply ? null : remoteTrack?.PlayedAt);
        await store.WriteAsync(userId, Key, state, ct).ConfigureAwait(false);
        return Review(state);
    }

    private async Task ReconcileAsync(Guid userId, LocalMusic local, MusicTrack? remoteTrack, FavouriteState state,
        List<FavouriteAddition> additions, CancellationToken ct)
    {
        var remote = remoteTrack is not null;
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
        if (pending is not null && (pending.LocalRevision != local.FavouriteRevision || pending.RemoteLovedAt != remoteTrack?.PlayedAt))
        {
            state.Pending.Remove(pending);
            state.Pending.Add(new(Guid.NewGuid(), local.Id, local.Track, pending.RemoveFrom, local.FavouriteRevision, remoteTrack?.PlayedAt));
        }
        if (pending is null && previous is not null && local.Favourite != remote)
        {
            if ((previous.Local || previous.LocalRevision != local.FavouriteRevision) && !local.Favourite && remote)
                state.Pending.Add(new(Guid.NewGuid(), local.Id, local.Track, "lastfm", local.FavouriteRevision, remoteTrack?.PlayedAt));
            else if (previous.Remote && !remote && local.Favourite)
                state.Pending.Add(new(Guid.NewGuid(), local.Id, local.Track, "jellyfin", local.FavouriteRevision, remoteTrack?.PlayedAt));
        }
        if (!state.Pending.Any(p => p.ItemId == local.Id))
        {
            if (local.Favourite && !remote && (previous is null || !previous.Local))
            {
                await StageAdditionAsync(userId, local, "lastfm", additions, ct).ConfigureAwait(false);
                await api.SetLovedAsync(userId, local.Track, true, ct).ConfigureAwait(false);
                remote = true;
            }
            else if (!local.Favourite && remote && (previous is null || !previous.Remote))
            {
                await StageAdditionAsync(userId, local, "jellyfin", additions, ct).ConfigureAwait(false);
                writer.TrySetFavourite(userId, local.Id, true, local.FavouriteRevision, ct);
                local = library.Get(userId, local.Id, ct);
                if (!local.Favourite) state.Pending.Add(new(Guid.NewGuid(), local.Id, local.Track, "lastfm", local.FavouriteRevision, remoteTrack?.PlayedAt));
            }
        }
        state.Observed[local.Id] = new(local.Track, local.Favourite, remote, local.FavouriteRevision, remoteTrack?.PlayedAt);
    }

    private async Task StageAdditionAsync(Guid userId, LocalMusic local, string target, List<FavouriteAddition> additions, CancellationToken ct)
    {
        additions.Add(new(local.Id, local.Track, target, local.FavouriteRevision));
        await store.WriteAsync(userId, AdditionsKey, additions, ct).ConfigureAwait(false);
    }

    private async Task RecoverAdditionsAsync(Guid userId, FavouriteState state, Dictionary<Guid, MusicTrack> remote, CancellationToken ct)
    {
        var additions = await store.ReadAsync<List<FavouriteAddition>>(userId, AdditionsKey, ct).ConfigureAwait(false);
        if (additions is null) return;
        if (additions.Count > 200) throw new InvalidDataException("Favourite recovery journal exceeds its operation limit.");
        foreach (var addition in additions)
        {
            if (library.Match(userId, addition.Track, ct).ItemId != addition.ItemId) continue;
            var local = library.Get(userId, addition.ItemId, ct);
            var remotelyLoved = remote.TryGetValue(local.Id, out var loved);
            var wasAdded = addition.Target == "lastfm" ? remotelyLoved
                : local.Favourite || local.FavouriteRevision != addition.LocalRevision;
            state.Observed[local.Id] = new(local.Track, wasAdded, wasAdded && remotelyLoved,
                addition.LocalRevision, loved?.PlayedAt);
        }
        await store.WriteAsync(userId, Key, state, ct).ConfigureAwait(false);
        await store.DeleteAsync(userId, AdditionsKey, ct).ConfigureAwait(false);
    }

    private (Dictionary<Guid, MusicTrack> Matched, HashSet<string> Unresolved) Resolve(Guid userId, IReadOnlyList<MusicTrack> tracks, CancellationToken ct)
    {
        var result = new Dictionary<Guid, MusicTrack>();
        var unresolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var track in tracks)
        {
            if (library.Match(userId, track, ct).ItemId is { } id) result.TryAdd(id, track);
            else unresolved.Add(MusicFeatureService.TrackKey(track));
        }
        return (result, unresolved);
    }

    private async Task<FavouriteState> ReadAsync(Guid id, CancellationToken ct) =>
        await store.ReadAsync<FavouriteState>(id, Key, ct).ConfigureAwait(false) ?? new(false, [], []);

    private static FavouriteReview Review(FavouriteState state) => new(state.Enabled, state.Pending, state.LastSync, state.Status);
}
