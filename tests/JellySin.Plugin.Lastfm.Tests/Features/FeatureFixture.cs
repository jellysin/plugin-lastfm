using System.Text.Json;
using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Tests.Core;

namespace JellySin.Plugin.Lastfm.Tests.Features;

internal sealed class FeatureFixture : IDisposable
{
    public CoreFixture Core { get; } = new();
    public FeatureLocks Locks { get; } = new();
    public FakeMusicLibrary Library { get; } = new();
    public MusicApi Api { get; }
    public FavouritesService Favourites { get; }
    public MusicFeatureService History { get; }
    public List<MusicTrack> Loved { get; } = [];
    public List<MusicTrack> Top { get; } = [];
    public List<MusicTrack> Recent { get; } = [];
    public bool IncompleteLoved { get; set; }

    public FeatureFixture()
    {
        Api = new(Core.Client, Core.Accounts, Core.Clock);
        Favourites = new(Api, Library, Library, Core.Store, Locks, Core.Clock, Core.Accounts);
        History = new(Api, Library, Library, Core.Store, Locks, Core.Clock, Core.Accounts);
    }

    public async Task InitializeAsync()
    {
        await Core.ConnectAsync();
        Library.UserId = Core.UserId;
        Core.Client.Calls.Clear();
        Core.Client.Handler = Respond;
    }

    public LocalMusic AddLocal(string title, bool favourite = false, int playCount = 0)
    {
        var value = new LocalMusic(Guid.NewGuid(), new MusicTrack("Artist", title), favourite, playCount, null);
        Library.Items[value.Id] = value;
        return value;
    }

    public JsonDocument Respond(string method, IReadOnlyDictionary<string, string> parameters, string? session)
    {
        if (method is "track.love" or "track.unlove")
        {
            Assert.Equal("private-session-value", session);
            Loved.RemoveAll(t => t.Artist == parameters["artist"] && t.Title == parameters["track"]);
            if (method == "track.love") Loved.Add(new(parameters["artist"], parameters["track"]));
            return JsonDocument.Parse("{}");
        }
        return method switch
        {
            "user.getLovedTracks" => Page("lovedtracks", Loved, IncompleteLoved ? 51 : 1),
            "user.getTopTracks" => Page("toptracks", Top),
            "user.getRecentTracks" => Page("recenttracks", Recent),
            _ => JsonDocument.Parse("{}"),
        };
    }

    public static JsonDocument Page(string root, IReadOnlyList<MusicTrack> tracks, int totalPages = 1, int page = 1) =>
        JsonSerializer.SerializeToDocument(new Dictionary<string, object>
        {
            [root] = new Dictionary<string, object>
            {
                ["@attr"] = new { page, totalPages, total = tracks.Count },
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

    public void Dispose() { Locks.Dispose(); Core.Dispose(); }
}

internal sealed class FakeMusicLibrary : IMusicLibrary, IMusicWriter
{
    public Guid UserId { get; set; }
    public Dictionary<Guid, LocalMusic> Items { get; } = [];
    public List<(Guid Item, bool Favourite)> FavouriteWrites { get; } = [];
    public List<(Guid Item, int Count, DateTime? Date)> HistoryWrites { get; } = [];

    public MusicMatch Match(Guid userId, MusicTrack track, CancellationToken ct)
    {
        Validate(userId, ct);
        var matches = Items.Values.Where(v => v.Track.Artist == track.Artist && v.Track.Title == track.Title).ToArray();
        return new(track, matches.Length == 1 ? matches[0].Id : null, matches.Length == 0 ? "missing" : matches.Length == 1 ? "matched" : "ambiguous");
    }

    public LocalMusic Get(Guid userId, Guid itemId, CancellationToken ct) { Validate(userId, ct); return Items[itemId]; }
    public IReadOnlyList<LocalMusic> GetFavourites(Guid userId, CancellationToken ct) { Validate(userId, ct); return Items.Values.Where(v => v.Favourite).ToArray(); }
    public void SetFavourite(Guid userId, Guid itemId, bool favourite, CancellationToken ct)
    {
        Validate(userId, ct);
        FavouriteWrites.Add((itemId, favourite));
        Items[itemId] = Items[itemId] with { Favourite = favourite };
    }

    public void ApplyHistoryFloor(Guid userId, Guid itemId, int playCount, DateTime? lastPlayed, CancellationToken ct)
    {
        Validate(userId, ct);
        HistoryWrites.Add((itemId, playCount, lastPlayed));
    }

    private void Validate(Guid id, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Assert.Equal(UserId, id); }
}
