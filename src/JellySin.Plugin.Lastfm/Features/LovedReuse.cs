using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using MediaBrowser.Controller.Library;

namespace JellySin.Plugin.Lastfm.Features;

/// <summary>What a library match depends on besides the track: the library's items and the user's access.</summary>
public interface ILibraryGeneration
{
    /// <summary>A counter that moves whenever a library item is added, updated or removed.</summary>
    long Current { get; }

    /// <summary>A fingerprint of the user's policy, which decides the libraries, ratings and tags a match can see.</summary>
    string Access(Guid userId);
}

/// <summary>
/// Counts Jellyfin's item events. Jellyfin 12.0 announces no policy change, so access is compared by fingerprint instead.
/// </summary>
public sealed class LibraryGeneration : ILibraryGeneration, IDisposable
{
    private readonly ILibraryManager library;
    private readonly IUserManager users;
    private long current;

    public LibraryGeneration(ILibraryManager library, IUserManager users)
    {
        this.library = library;
        this.users = users;
        library.ItemAdded += OnItemChanged;
        library.ItemUpdated += OnItemChanged;
        library.ItemRemoved += OnItemChanged;
    }

    public long Current => Interlocked.Read(ref current);

    // Item callbacks only count; they never wait.
    public void Advance() => Interlocked.Increment(ref current);

    public string Access(Guid userId)
    {
        if (users.GetUserById(userId) is not { } user) return string.Empty;
        var policy = JsonSerializer.SerializeToUtf8Bytes(users.GetUserDto(user).Policy);
        return Convert.ToHexString(SHA256.HashData(policy));
    }

    public void Dispose()
    {
        library.ItemAdded -= OnItemChanged;
        library.ItemUpdated -= OnItemChanged;
        library.ItemRemoved -= OnItemChanged;
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs args) => Advance();
}

/// <summary>
/// What a favourite run established, kept for the next run while neither side changed: the complete loved collection
/// and the library match of each loved track. Bounded in users, tracks and age; held in memory only.
/// </summary>
public sealed class LovedReuse(ILibraryGeneration generation, TimeProvider clock)
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
            if (set.Generation != current || set.Access != access || now - set.Created >= MaxAge) set.Reset(current, access, now);
            for (var index = 0; index < tracks.Count; index++)
            {
                var key = MatchKey.Of(tracks[index]);
                if (!set.Items.TryGetValue(key, out var id))
                {
                    id = match(tracks[index]);
                    if (set.Items.Count < MaxTracks) set.Items[key] = id;
                }
                result[index] = id;
            }
        }
        Trim(matches, entry => entry.Created);
        return result;
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
        public static MatchKey Of(MusicTrack track) => new(track.Artist, track.Title, track.Album, track.MusicBrainzId);
    }

    private sealed class MatchSet(long generation, string access, DateTimeOffset created)
    {
        public long Generation { get; private set; } = generation;
        public string Access { get; private set; } = access;
        public DateTimeOffset Created { get; private set; } = created;
        public Dictionary<MatchKey, Guid?> Items { get; } = [];

        public void Reset(long nextGeneration, string nextAccess, DateTimeOffset now)
        {
            Generation = nextGeneration;
            Access = nextAccess;
            Created = now;
            Items.Clear();
        }
    }
}
