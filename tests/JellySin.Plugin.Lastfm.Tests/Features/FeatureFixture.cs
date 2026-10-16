using System.Globalization;
using System.Text.Json;
using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Tests.Core;
using Microsoft.Extensions.Logging.Testing;

namespace JellySin.Plugin.Lastfm.Tests.Features;

internal sealed class FeatureFixture : IDisposable
{
    public CoreFixture Core { get; } = new();
    public FeatureLocks Locks { get; } = new();
    public FakeMusicLibrary Library { get; } = new();
    public MusicApi Api { get; }
    public FavouritesService Favourites { get; }
    public FavouriteRuns Runs { get; }
    public LovedReuse Reuse { get; }
    public MusicFeatureService History { get; }
    public List<MusicTrack> Loved { get; } = [];
    public List<MusicTrack> Top { get; } = [];
    public List<MusicTrack> Recent { get; } = [];
    public bool IncompleteLoved { get; set; }
    public FakeLogger<FavouritesService> FavouriteLog { get; } = new();

    public FeatureFixture()
    {
        Api = new(Core.Client, Core.Accounts, Core.Clock);
        Reuse = new(Library, Core.Clock);
        Favourites = new(Api, Library, Library, Core.Store, Locks, Core.Clock, Core.Accounts, FavouriteLog, Reuse);
        Runs = new(Favourites, Core.Clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<FavouriteRuns>.Instance);
        History = new(Api, Library, Library, Core.Store, Locks, Core.Clock, Core.Accounts);
    }

    public async Task InitializeAsync()
    {
        await Core.ConnectAsync();
        Library.UserId = Core.UserId;
        Core.Client.Calls.Clear();
        Core.Client.Handler = Respond;
    }

    /// <summary>The call behind the page's Save control, with the token of that web request.</summary>
    public Task<FavouriteReview> SaveFromRequestAsync(bool enabled, CancellationToken request) =>
        Runs.SetEnabledAsync(Core.UserId, enabled, request);

    /// <summary>Completes when no reconciliation runs for the user any more. It starts none.</summary>
    public async Task ReconciliationEndedAsync(CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (Runs.IsRunning(Core.UserId))
        {
            Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), "The reconciliation did not end within ten seconds.");
            await Task.Delay(10, ct).ConfigureAwait(false);
        }
    }

    public async Task<FavouriteReview> EnableAndSyncAsync(CancellationToken ct)
    {
        await Favourites.SetEnabledAsync(Core.UserId, true, ct).ConfigureAwait(false);
        return await Favourites.SyncAsync(Core.UserId, ct).ConfigureAwait(false);
    }

    public LocalMusic AddLocal(string title, bool favourite = false, int playCount = 0, string? mbid = null, string artist = "Artist")
    {
        var value = new LocalMusic(Guid.NewGuid(), new MusicTrack(artist, title, MusicBrainzId: mbid), favourite, playCount, null);
        Library.Items[value.Id] = value;
        return value;
    }

    public JsonDocument Respond(string method, IReadOnlyDictionary<string, string> parameters, string? session)
    {
        if (method is "track.love" or "track.unlove")
        {
            Assert.Equal("private-session-value", session);
            Loved.RemoveAll(t => t.Artist == parameters["artist"] && t.Title == parameters["track"]);
            if (method == "track.love") Loved.Add(new(parameters["artist"], parameters["track"], PlayedAt: Core.Clock.GetUtcNow()));
            return JsonDocument.Parse("{}");
        }
        return method switch
        {
            "user.getLovedTracks" => IncompleteLoved ? Page("lovedtracks", Loved, 51) : LovedPage(parameters),
            "user.getTopTracks" => Page("toptracks", Top),
            "user.getRecentTracks" => Page("recenttracks", Recent),
            _ => JsonDocument.Parse("{}"),
        };
    }

    /// <summary>The loved tracks 200 to a page, as Last.fm serves them to the plugin's requests.</summary>
    private JsonDocument LovedPage(IReadOnlyDictionary<string, string> parameters)
    {
        var page = int.Parse(parameters["page"], CultureInfo.InvariantCulture);
        return Page("lovedtracks", Loved.Skip((page - 1) * 200).Take(200).ToList(), Math.Max(1, (Loved.Count + 199) / 200), page, Loved.Count);
    }

    public static JsonDocument Page(string root, IReadOnlyList<MusicTrack> tracks, int totalPages = 1, int page = 1, int? total = null) =>
        JsonSerializer.SerializeToDocument(new Dictionary<string, object>
        {
            [root] = new Dictionary<string, object>
            {
                ["@attr"] = new { page, totalPages, total = total ?? tracks.Count },
                ["track"] = tracks.Select(t => new Dictionary<string, object?>
                {
                    ["artist"] = new { name = t.Artist },
                    ["name"] = t.Title,
                    ["album"] = t.Album,
                    ["mbid"] = t.MusicBrainzId,
                    ["url"] = t.Url,
                    ["playcount"] = t.PlayCount,
                    ["date"] = t.PlayedAt is null ? null : new { uts = t.PlayedAt.Value.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    ["@attr"] = new { nowplaying = t.NowPlaying ? "true" : "false" },
                }).ToArray(),
            },
        });

    public void Dispose() { Runs.Dispose(); Locks.Dispose(); Core.Dispose(); }
}

internal sealed class FakeMusicLibrary : IMusicLibrary, IMusicWriter, ILibraryGeneration
{
    public Guid UserId { get; set; }
    /// <summary>Stands in for Jellyfin's item events: every added, removed or renamed item moves it, favourite flags do not.</summary>
    public long Current => unchecked(Items.Count + Items.Sum(entry => (long)HashCode.Combine(entry.Key, entry.Value.Track)));
    /// <summary>Stands in for the user's policy fingerprint.</summary>
    public string Policy { get; set; } = "all-libraries";
    public string Access(Guid userId) => Policy;
    public int MatchCalls { get; private set; }
    public Dictionary<Guid, LocalMusic> Items { get; } = [];
    public List<(Guid Item, bool Favourite)> FavouriteWrites { get; } = [];
    public List<(Guid Item, int Count, DateTime? Date)> HistoryWrites { get; } = [];
    public Action<CancellationToken>? OnMatch { get; set; }

    public MusicMatch Match(Guid userId, MusicTrack track, CancellationToken ct)
    {
        Validate(userId, ct);
        MatchCalls++;
        OnMatch?.Invoke(ct);
        if (Guid.TryParse(track.MusicBrainzId, out var mbid))
        {
            var mbidMatches = Items.Values
                .Where(v => Guid.TryParse(v.Track.MusicBrainzId, out var existing) && existing == mbid)
                .ToArray();
            if (mbidMatches.Length > 0)
                return new(track, mbidMatches.Length == 1 ? mbidMatches[0].Id : null,
                    mbidMatches.Length == 1 ? "matched" : "ambiguous");
        }
        var matches = Items.Values.Where(v => v.Track.Artist == track.Artist && v.Track.Title == track.Title).ToArray();
        return new(track, matches.Length == 1 ? matches[0].Id : null, matches.Length == 0 ? "missing" : matches.Length == 1 ? "matched" : "ambiguous");
    }

    public LocalMusic Get(Guid userId, Guid itemId, CancellationToken ct) { Validate(userId, ct); return Items[itemId]; }
    public IReadOnlyList<LocalMusic> GetFavourites(Guid userId, CancellationToken ct) { Validate(userId, ct); return Items.Values.Where(v => v.Favourite).ToArray(); }
    public void SetFavourite(Guid userId, Guid itemId, bool favourite, CancellationToken ct)
    {
        Validate(userId, ct);
        FavouriteWrites.Add((itemId, favourite));
        Items[itemId] = Items[itemId] with { Favourite = favourite, FavouriteRevision = Guid.NewGuid().ToString("N") };
    }

    public bool TrySetFavourite(Guid userId, Guid itemId, bool favourite, string expectedRevision, CancellationToken ct)
    {
        if (Items[itemId].FavouriteRevision != expectedRevision) return false;
        SetFavourite(userId, itemId, favourite, ct);
        return true;
    }

    public void ApplyHistoryFloor(Guid userId, Guid itemId, int playCount, DateTime? lastPlayed, CancellationToken ct)
    {
        Validate(userId, ct);
        HistoryWrites.Add((itemId, playCount, lastPlayed));
    }

    private void Validate(Guid id, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Assert.Equal(UserId, id); }
}
