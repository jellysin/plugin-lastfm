using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Features;

/// <summary>What a library match depends on besides the track: the library's items and the user's access.</summary>
public interface ILibraryGeneration
{
    /// <summary>A counter that moves whenever a library item is added, updated or removed.</summary>
    long Current { get; }

    /// <summary>A fingerprint of the user's policy, which decides the libraries, ratings and tags a match can see.</summary>
    string Access(Guid userId);

    /// <summary>The tracks changed after <paramref name="generation"/>, or null when they are no longer all known.</summary>
    LibraryChanges? ChangesSince(long generation);
}

/// <summary>
/// What changed in the library: the names and MusicBrainz ids of added or updated tracks, which can create a match or
/// make one ambiguous, and the ids of updated or removed tracks, which can end one.
/// </summary>
public sealed class LibraryChanges
{
    public HashSet<string> Titles { get; } = [];
    public HashSet<Guid> Mbids { get; } = [];
    public HashSet<Guid> Items { get; } = [];

    public void Note(Guid itemId, string? title, string? mbid, bool present)
    {
        Items.Add(itemId);
        if (!present) return;
        Titles.Add(MusicLibrary.Normalize(title));
        if (Guid.TryParse(mbid, out var id)) Mbids.Add(id);
    }

    /// <summary>Whether a kept result for <paramref name="track"/>, pending when <paramref name="item"/> is null, may no longer hold.</summary>
    public bool Touches(MusicTrack track, Guid? item) => item is null || Items.Contains(item.Value)
        || Titles.Contains(MusicLibrary.Normalize(track.Title))
        || (Guid.TryParse(track.MusicBrainzId, out var mbid) && Mbids.Contains(mbid));
}

/// <summary>
/// Counts Jellyfin's item events and keeps the last 4096 track changes, so a scan forgets only the matches it touched.
/// Jellyfin 12.0 announces no policy change, so access is compared by fingerprint instead.
/// </summary>
public sealed class LibraryGeneration : ILibraryGeneration, IDisposable
{
    public const int MaxChanges = 4096;
    private readonly ILibraryManager library;
    private readonly IUserManager users;
    private readonly object gate = new();
    private readonly Queue<Change> changes = new();
    private long current;
    // The changes up to this generation are no longer kept.
    private long forgotten;

    public LibraryGeneration(ILibraryManager library, IUserManager users)
    {
        this.library = library;
        this.users = users;
        library.ItemAdded += OnItemAdded;
        library.ItemUpdated += OnItemUpdated;
        library.ItemRemoved += OnItemRemoved;
    }

    public long Current => Interlocked.Read(ref current);

    /// <summary>Moves the generation for a change that is not known, so every kept match is matched again.</summary>
    public void Advance() => Record(null);

    public string Access(Guid userId)
    {
        if (users.GetUserById(userId) is not { } user) return string.Empty;
        var policy = JsonSerializer.SerializeToUtf8Bytes(users.GetUserDto(user).Policy);
        return Convert.ToHexString(SHA256.HashData(policy));
    }

    public LibraryChanges? ChangesSince(long generation)
    {
        lock (gate)
        {
            if (generation < forgotten) return null;
            var result = new LibraryChanges();
            foreach (var change in changes.Where(change => change.Generation > generation))
            {
                if (change.Item is not { } item) return null;
                result.Note(item, change.Title, change.Mbid, change.Present);
            }
            return result;
        }
    }

    public void Dispose()
    {
        library.ItemAdded -= OnItemAdded;
        library.ItemUpdated -= OnItemUpdated;
        library.ItemRemoved -= OnItemRemoved;
    }

    // Item callbacks only record in memory; they never wait.
    private void OnItemAdded(object? sender, ItemChangeEventArgs args) => OnItemChanged(args.Item, true);

    private void OnItemUpdated(object? sender, ItemChangeEventArgs args) => OnItemChanged(args.Item, true);

    private void OnItemRemoved(object? sender, ItemChangeEventArgs args) => OnItemChanged(args.Item, false);

    private void OnItemChanged(BaseItem? item, bool present)
    {
        switch (item)
        {
            case Audio audio:
                Record(new(0, audio.Id, audio.Name, audio.ProviderIds.GetValueOrDefault("MusicBrainzTrack"), present));
                break;
            // A removed folder may take tracks with it; matching everything again does not rely on their own events.
            case Folder when !present:
            case null:
                Record(null);
                break;
            default:
                Interlocked.Increment(ref current);
                break;
        }
    }

    private void Record(Change? change)
    {
        lock (gate)
        {
            var generation = Interlocked.Increment(ref current);
            changes.Enqueue((change ?? new(0, null, null, null, false)) with { Generation = generation });
            if (changes.Count > MaxChanges) forgotten = changes.Dequeue().Generation;
        }
    }

    private sealed record Change(long Generation, Guid? Item, string? Title, string? Mbid, bool Present);
}

/// <summary>
/// What a favourite run established, kept for the next run while neither side changed: the complete loved collection
/// and the library match of each loved track. Bounded in users, tracks and age; held in memory only.
/// </summary>
public sealed class LovedReuse(ILibraryGeneration generation, TimeProvider clock, ILogger<LovedReuse>? logger = null)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    public const int MaxUsers = 8;
    public const int MaxTracks = 10_000;
    private readonly ConcurrentDictionary<Guid, LovedSnapshot> loved = new();
    private readonly ConcurrentDictionary<Guid, MatchSet> matches = new();

    /// <summary>
    /// The kept collection when a fresh page 1 equals the page 1 it was read with: the same total, page count and tracks
    /// with their loved dates. A new love, this plugin's included, lands on page 1 and a removal changes the total.
    /// </summary>
    public MusicPage? Reuse(Guid userId, string username, MusicPage first)
    {
        if (!loved.TryGetValue(userId, out var kept) || kept.Username != username || clock.GetUtcNow() - kept.Taken >= MaxAge) return null;
        return first.TotalTracks == kept.First.TotalTracks && first.TotalPages == kept.First.TotalPages
            && first.Tracks.SequenceEqual(kept.First.Tracks) ? kept.Collection : null;
    }

    /// <summary>Keeps a complete collection whose own first page is <paramref name="first"/>; anything else drops the kept one.</summary>
    public void Keep(Guid userId, string username, MusicPage first, MusicPage collection)
    {
        if (!collection.Complete || collection.Tracks.Count > MaxTracks || first.TotalPages != collection.TotalPages
            || !first.Tracks.SequenceEqual(collection.Tracks.Take(first.Tracks.Count)))
        {
            loved.TryRemove(userId, out _);
            return;
        }
        loved[userId] = new(username, first, collection, clock.GetUtcNow());
        Trim(loved, entry => entry.Taken);
    }

    /// <summary>The library item of each track, matched again when the library or the user's access moved, the set aged out or the track is new.</summary>
    public Guid?[] Match(Guid userId, IReadOnlyList<MusicTrack> tracks, Func<MusicTrack, Guid?> match)
    {
        // Read before matching: a change during this run makes the next run match again.
        var current = generation.Current;
        var access = generation.Access(userId);
        var now = clock.GetUtcNow();
        var set = matches.GetOrAdd(userId, _ => new(current, access, now));
        var result = new Guid?[tracks.Count];
        lock (set)
        {
            if (set.Access != access || now - set.Created >= MaxAge) set.Reset(current, access, now);
            else if (set.Generation != current) Refresh(set, current, access, now);
            for (var index = 0; index < tracks.Count; index++)
            {
                var track = tracks[index];
                Guid? id;
                var hasMbid = Guid.TryParse(track.MusicBrainzId, out var mbid);
                if (hasMbid && set.Mbids.TryGetValue(mbid, out var mbidId))
                {
                    id = mbidId;
                }
                else
                {
                    var key = MatchKey.Of(track);
                    if (!set.Items.TryGetValue(key, out id))
                    {
                        id = match(track);
                        if (set.Items.Count < MaxTracks) set.Items[key] = id;
                    }
                    if (hasMbid && id is { } matchedId && set.Mbids.Count < MaxTracks)
                    {
                        set.Mbids.TryAdd(mbid, matchedId);
                    }
                }
                result[index] = id;
            }
        }
        Trim(matches, entry => entry.Created);
        return result;
    }

    /// <summary>
    /// After library changes, forgets the pending tracks and the matches the changes touch; the other matches still hold.
    /// When the changes are no longer all known, it forgets every match.
    /// </summary>
    private void Refresh(MatchSet set, long current, string access, DateTimeOffset now)
    {
        if (generation.ChangesSince(set.Generation) is { } changes)
        {
            set.Forget(changes, current);
            return;
        }
        logger?.LogInformation("The library changed more than {Changes} times or lost a folder; all {Tracks} kept favourite matches are matched again.",
            LibraryGeneration.MaxChanges, set.Items.Count);
        set.Reset(current, access, now);
    }

    private static void Trim<T>(ConcurrentDictionary<Guid, T> entries, Func<T, DateTimeOffset> age)
    {
        while (entries.Count > MaxUsers)
        {
            var oldest = entries.MinBy(entry => age(entry.Value));
            entries.TryRemove(oldest.Key, out _);
        }
    }

    private sealed record LovedSnapshot(string Username, MusicPage First, MusicPage Collection, DateTimeOffset Taken);

    private readonly record struct MatchKey(string Artist, string Title, string? Album, string? MusicBrainzId)
    {
        public static MatchKey Of(MusicTrack track) => new(track.Artist, track.Title, track.Album,
            Guid.TryParse(track.MusicBrainzId, out var mbid) ? mbid.ToString() : track.MusicBrainzId);

        public MusicTrack Track => new(Artist, Title, Album, MusicBrainzId);
    }

    private sealed class MatchSet(long generation, string access, DateTimeOffset created)
    {
        public long Generation { get; private set; } = generation;
        public string Access { get; private set; } = access;
        public DateTimeOffset Created { get; private set; } = created;
        public Dictionary<MatchKey, Guid?> Items { get; } = [];
        public Dictionary<Guid, Guid> Mbids { get; } = [];

        /// <summary>Keeps the age of the set, so every kept match is still matched again once a day.</summary>
        public void Forget(LibraryChanges changes, long nextGeneration)
        {
            Generation = nextGeneration;
            foreach (var entry in Items.Where(entry => changes.Touches(entry.Key.Track, entry.Value)).ToArray()) Items.Remove(entry.Key);
            foreach (var entry in Mbids.Where(entry => changes.Mbids.Contains(entry.Key) || changes.Items.Contains(entry.Value)).ToArray())
                Mbids.Remove(entry.Key);
        }

        public void Reset(long nextGeneration, string nextAccess, DateTimeOffset now)
        {
            Generation = nextGeneration;
            Access = nextAccess;
            Created = now;
            Items.Clear();
            Mbids.Clear();
        }
    }
}
