using JellySin.Plugin.Lastfm.Configuration;
namespace JellySin.Plugin.Lastfm.Features;

public sealed class DiscoveryService(MusicApi api, IMusicLibrary library, IDiscoveryLibrary entities, AccountService accounts)
{
    public Task<IReadOnlyList<MusicSeed>> SearchSeedsAsync(Guid userId, string query, CancellationToken ct)
    {
        using var operation = accounts.LinkOperation(userId, ct);
        return Task.FromResult(entities.Search(userId, query, operation.Token));
    }

    public async Task<DiscoveryResult> GetAsync(Guid userId, Guid? seedItemId, CancellationToken ct)
    {
        using var accountOperation = accounts.LinkOperation(userId, ct);
        ct = accountOperation.Token;
        IReadOnlyList<MusicTrack> seeds;
        if (seedItemId is { } id)
        {
            var seed = entities.GetSeed(userId, id, ct);
            seeds = seed.Kind == "track" ? [library.Get(userId, id, ct).Track]
                : await api.ArtistTracksAsync(seed.Name, ct).ConfigureAwait(false);
        }
        else seeds = (await api.GetPageAsync(userId, "user.getTopTracks", 1, "1month", ct).ConfigureAwait(false)).Tracks.Take(5).ToArray();
        var results = new Dictionary<string, MusicTrack>(StringComparer.Ordinal);
        var seedKeys = seeds.Select(MusicFeatureService.TrackKey).ToHashSet(StringComparer.Ordinal);
        foreach (var seed in seeds)
        {
            foreach (var track in await api.SimilarAsync(seed, ct).ConfigureAwait(false))
            {
                var key = MusicFeatureService.TrackKey(track);
                if (!seedKeys.Contains(key)) results.TryAdd(key, track);
            }
        }
        var matches = results.Values.Take(200).Select(t => library.Match(userId, t, ct)).ToArray();
        var artists = new Dictionary<string, DiscoveryEntity>(StringComparer.OrdinalIgnoreCase);
        foreach (var artist in seeds.Select(t => t.Artist).Distinct(StringComparer.OrdinalIgnoreCase).Take(3))
            foreach (var result in await api.ArtistDiscoveryAsync(artist, false, ct).ConfigureAwait(false)) artists.TryAdd(result.Name, result);
        var albums = new List<DiscoveryEntity>();
        foreach (var artist in artists.Values.Take(3))
            albums.AddRange(await api.ArtistDiscoveryAsync(artist.Name, true, ct).ConfigureAwait(false));
        return new(matches.Where(m => m.ItemId.HasValue).ToArray(), matches.Where(m => m.Status == "missing").Select(m => m.Track).ToArray(),
            artists.Values.Take(20).Select(a => entities.MatchEntity(userId, a, ct)).ToArray(),
            albums.Take(30).Select(a => entities.MatchEntity(userId, a, ct)).ToArray());
    }
}
