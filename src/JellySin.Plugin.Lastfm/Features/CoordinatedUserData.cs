using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace JellySin.Plugin.Lastfm.Features;

/// <summary>Serializes native userdata writes with imports and recognizes snapshots read before an import.</summary>
public sealed class CoordinatedUserData(IUserDataManager inner, Func<User, BaseItem, UserItemData?>? readCurrent = null) : IUserDataManager
{
    private readonly object[] gates = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();
    private readonly ConcurrentDictionary<(Guid User, Guid Item), WeakReference<Revision>> revisions = new();
    private readonly ConditionalWeakTable<UserItemData, Snapshot> snapshots = new();
    private readonly ConcurrentDictionary<(Guid User, Guid Item), long> favouriteRevisions = new();
    private readonly string favouriteEpoch = Guid.NewGuid().ToString("N");
    private volatile bool favouriteCapacityReached;
    private volatile bool historyCapacityReached;

    public event EventHandler<UserDataSaveEventArgs>? UserDataSaved
    {
        add => inner.UserDataSaved += value;
        remove => inner.UserDataSaved -= value;
    }

    public string FavouriteRevision(User user, BaseItem item)
    {
        lock (Gate(user))
        {
            if (favouriteCapacityReached) throw new InvalidOperationException("Favourite edit tracking capacity reached; restart before reviewing removals.");
            return favouriteEpoch + ":" + favouriteRevisions.GetValueOrDefault((user.Id, item.Id)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public (UserItemData? Value, string Revision) GetFavouriteSnapshot(User user, BaseItem item)
    {
        lock (Gate(user)) return (GetUserData(user, item), FavouriteRevision(user, item));
    }

    public void ApplyHistoryFloor(User user, BaseItem item, int count, DateTime? date, CancellationToken ct)
    {
        lock (Gate(user))
        {
            ct.ThrowIfCancellationRequested();
            if (historyCapacityReached) throw new InvalidOperationException("History snapshot tracking capacity reached; restart before importing history.");
            var current = ReadCurrent(user, item) ?? new UserItemData { Key = item.GetUserDataKeys()[0] };
            MergeFloor(current, count, date);
            var revision = RevisionFor(user, item.Id) ?? throw new InvalidOperationException("History snapshot tracking capacity reached; restart before importing history.");
            inner.SaveUserData(user, item, Copy(current), UserDataSaveReason.Import, ct);
            revision.Version++;
            revision.Import = revision.Version;
        }
    }

    public bool TrySetFavourite(User user, BaseItem item, bool value, string expectedRevision, CancellationToken ct)
    {
        lock (Gate(user))
        {
            if (FavouriteRevision(user, item) != expectedRevision) return false;
            var current = ReadCurrent(user, item) ?? new UserItemData { Key = item.GetUserDataKeys()[0] };
            Track(user, item, current);
            current.IsFavorite = value;
            SaveUserData(user, item, current, UserDataSaveReason.UpdateUserData, ct);
            return true;
        }
    }

    public UserItemData? GetUserData(User user, BaseItem item)
    {
        if (item is not Audio) return inner.GetUserData(user, item);
        lock (Gate(user))
        {
            var value = inner.GetUserData(user, item) is { } found ? Copy(found) : null;
            Track(user, item, value);
            return value;
        }
    }

    public void SaveUserData(User user, BaseItem item, UserItemData value, UserDataSaveReason reason, CancellationToken ct)
    {
        if (item is not Audio) { inner.SaveUserData(user, item, value, reason, ct); return; }
        lock (Gate(user))
        {
            ct.ThrowIfCancellationRequested();
            var revision = RevisionFor(user, item.Id);
            var current = ReadCurrent(user, item);
            var stale = revision is not null && snapshots.TryGetValue(value, out var snapshot) && ReferenceEquals(snapshot.Revision, revision)
                && snapshot.Version < revision.Import;
            if (stale && current is not null) MergeFloor(value, current.PlayCount, current.LastPlayedDate);
            else if (revision is not null && current is not null && (value.PlayCount < current.PlayCount || value.LastPlayedDate < current.LastPlayedDate))
                revision.Import = 0; // A fresh native snapshot may intentionally reset listening history.
            inner.SaveUserData(user, item, Copy(value), reason, ct);
            if (current?.IsFavorite != value.IsFavorite) RecordFavourite(user, item);
            if (revision is not null) revision.Version++;
            Track(user, item, value);
        }
    }

    public void SaveUserData(User user, BaseItem item, UpdateUserItemDataDto value, UserDataSaveReason reason)
    {
        if (item is not Audio) { inner.SaveUserData(user, item, value, reason); return; }
        lock (Gate(user))
        {
            var current = ReadCurrent(user, item) ?? new UserItemData { Key = item.GetUserDataKeys()[0] };
            Track(user, item, current);
            ApplyUpdate(current, value);
            this.SaveUserData(user, item, current, reason, CancellationToken.None);
        }
    }

    public Dictionary<Guid, UserItemData> GetUserDataBatch(IReadOnlyList<BaseItem> items, User user)
    {
        lock (Gate(user))
        {
            var result = inner.GetUserDataBatch(items, user);
            foreach (var item in items.OfType<Audio>())
            {
                if (!result.TryGetValue(item.Id, out var value)) continue;
                result[item.Id] = Copy(value);
                Track(user, item, result[item.Id]);
            }
            return result;
        }
    }

    public VersionResumeData? GetResumeUserData(User user, BaseItem item) => inner.GetResumeUserData(user, item);

    public IReadOnlyDictionary<Guid, VersionResumeData> GetResumeUserDataBatch(IReadOnlyList<BaseItem> items, User user) => inner.GetResumeUserDataBatch(items, user);

    public UserItemDataDto? GetUserDataDto(BaseItem item, User user)
    {
        lock (Gate(user)) return inner.GetUserDataDto(item, user);
    }

    public UserItemDataDto? GetUserDataDto(BaseItem item, BaseItemDto? dto, User user, DtoOptions options)
    {
        lock (Gate(user)) return inner.GetUserDataDto(item, dto, user, options);
    }

    public bool UpdatePlayState(BaseItem item, UserItemData data, long? position) => inner.UpdatePlayState(item, data, position);

    public void ResetPlaybackStreamSelections(User user, BaseItem item)
    {
        lock (Gate(user)) inner.ResetPlaybackStreamSelections(user, item);
    }

    private object Gate(User user) => gates[(uint)user.Id.GetHashCode() % (uint)gates.Length];

    private UserItemData? ReadCurrent(User user, BaseItem item) => readCurrent is null ? inner.GetUserData(user, item) : readCurrent(user, item);

    private static UserItemData Copy(UserItemData value) => new()
    {
        Key = value.Key,
        PlayCount = value.PlayCount,
        LastPlayedDate = value.LastPlayedDate,
        Played = value.Played,
        IsFavorite = value.IsFavorite,
        PlaybackPositionTicks = value.PlaybackPositionTicks,
        Likes = value.Likes,
        Rating = value.Rating,
        AudioStreamIndex = value.AudioStreamIndex,
        SubtitleStreamIndex = value.SubtitleStreamIndex
    };

    private static void ApplyUpdate(UserItemData current, UpdateUserItemDataDto value)
    {
        current.PlaybackPositionTicks = value.PlaybackPositionTicks ?? current.PlaybackPositionTicks;
        current.PlayCount = value.PlayCount ?? current.PlayCount;
        current.IsFavorite = value.IsFavorite ?? current.IsFavorite;
        current.Likes = value.Likes ?? current.Likes;
        current.Played = value.Played ?? current.Played;
        current.LastPlayedDate = value.LastPlayedDate ?? current.LastPlayedDate;
        current.Rating = value.Rating ?? current.Rating;
    }

    private void RecordFavourite(User user, BaseItem item)
    {
        var key = (user.Id, item.Id);
        if (favouriteRevisions.ContainsKey(key) || favouriteRevisions.Count < 65536)
            favouriteRevisions.AddOrUpdate(key, 1, (_, value) => value + 1);
        else favouriteCapacityReached = true;
    }

    private Revision? RevisionFor(User user, Guid itemId)
    {
        var key = (user.Id, itemId);
        if (revisions.TryGetValue(key, out var reference) && reference.TryGetTarget(out var current)) return current;
        if (revisions.Count >= 65536)
        {
            foreach (var entry in revisions.Where(entry => !entry.Value.TryGetTarget(out _)).Take(65536))
                revisions.TryRemove(entry);
            if (revisions.Count >= 65536)
            {
                historyCapacityReached = true;
                return null; // Native reads and playback stay available; new imports fail closed.
            }
        }
        var created = new Revision();
        revisions[key] = new(created);
        return created;
    }

    private void Track(User user, BaseItem item, UserItemData? value)
        => TrackSnapshot(user, item.Id, value);

    private void TrackSnapshot(User user, Guid itemId, UserItemData? value)
    {
        if (value is null) return;
        var revision = RevisionFor(user, itemId);
        if (revision is not null) snapshots.AddOrUpdate(value, new(revision, revision.Version));
    }

    private static void MergeFloor(UserItemData state, int count, DateTime? date)
    {
        state.PlayCount = Math.Max(state.PlayCount, count);
        if (date.HasValue && (!state.LastPlayedDate.HasValue || date > state.LastPlayedDate)) state.LastPlayedDate = date;
        if (state.PlayCount > 0) state.Played = true;
    }

    private sealed class Revision
    {
        public long Version { get; set; }
        public long Import { get; set; }
    }

    private sealed record Snapshot(Revision Revision, long Version);
}
