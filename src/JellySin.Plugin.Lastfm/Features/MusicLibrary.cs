using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace JellySin.Plugin.Lastfm.Features;

public sealed record LocalMusic(Guid Id, MusicTrack Track, bool Favourite, int PlayCount, DateTime? LastPlayed, string FavouriteRevision = "");

public interface IMusicLibrary
{
    MusicMatch Match(Guid userId, MusicTrack track, CancellationToken ct);
    LocalMusic Get(Guid userId, Guid itemId, CancellationToken ct);
    IReadOnlyList<LocalMusic> GetFavourites(Guid userId, CancellationToken ct);
}

public interface IMusicWriter
{
    void SetFavourite(Guid userId, Guid itemId, bool favourite, CancellationToken ct);
    bool TrySetFavourite(Guid userId, Guid itemId, bool favourite, string expectedRevision, CancellationToken ct);
    void ApplyHistoryFloor(Guid userId, Guid itemId, int playCount, DateTime? lastPlayed, CancellationToken ct);
}

public interface IDiscoveryLibrary
{
    IReadOnlyList<MusicSeed> Search(Guid userId, string query, CancellationToken ct);
    MusicSeed GetSeed(Guid userId, Guid itemId, CancellationToken ct);
    DiscoveryEntity MatchEntity(Guid userId, DiscoveryEntity entity, CancellationToken ct);
}

public sealed class MusicLibrary(ILibraryManager library, IUserManager users, IUserDataManager data) : IMusicLibrary, IMusicWriter, IDiscoveryLibrary
{
    public MusicMatch Match(Guid userId, MusicTrack track, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var query = Query(userId, 201);
        if (Guid.TryParse(track.MusicBrainzId, out var mbid))
        {
            query.HasAnyProviderId = new() { ["MusicBrainzTrack"] = mbid.ToString() };
            var ids = library.GetItemList(query).OfType<Audio>().ToArray();
            if (ids.Length > 0) return MatchResult(track, ids);
            query.HasAnyProviderId = null;
        }
        query.Name = track.Title;
        var candidates = library.GetItemList(query).OfType<Audio>().ToArray();
        ct.ThrowIfCancellationRequested();
        if (candidates.Length > 200) return new(track, null, "ambiguous");
        return MatchResult(track, candidates.Where(item => CompatibleId(track.MusicBrainzId, item.ProviderIds.GetValueOrDefault("MusicBrainzTrack"))
            && MatchesNames(track, item.Name, item.Artists, item.Album)).ToArray());
    }

    public static bool MatchesNames(MusicTrack track, string title, IReadOnlyList<string> artists, string? album) =>
        Normalize(track.Title) == Normalize(title) && artists.Any(a => Normalize(a) == Normalize(track.Artist))
        && (string.IsNullOrWhiteSpace(track.Album) || Normalize(track.Album) == Normalize(album));

    public IReadOnlyList<MusicSeed> Search(Guid userId, string query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (query.Trim().Length is < 2 or > 128) throw new ArgumentException("Search must contain 2–128 characters.", nameof(query));
        var request = Query(userId, 20);
        request.IncludeItemTypes = [BaseItemKind.Audio, BaseItemKind.MusicArtist];
        request.SearchTerm = query.Trim();
        return library.GetItemList(request).Select(ToSeed).ToArray();
    }

    public MusicSeed GetSeed(Guid userId, Guid itemId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var item = library.GetItemById<BaseItem>(itemId, userId);
        if (item is not (Audio or MusicArtist)) throw new KeyNotFoundException("Music seed is unavailable.");
        return ToSeed(item);
    }

    public DiscoveryEntity MatchEntity(Guid userId, DiscoveryEntity entity, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var query = Query(userId, 201);
        query.IncludeItemTypes = entity.Kind == "artist" ? [BaseItemKind.MusicArtist] : [BaseItemKind.MusicAlbum];
        if (entity.MusicBrainzId is { } mbid)
        {
            query.HasAnyProviderId = new() { [entity.Kind == "artist" ? "MusicBrainzArtist" : "MusicBrainzAlbum"] = mbid };
            var matches = library.GetItemList(query);
            if (matches.Count > 0) return entity with { ItemId = matches.Count == 1 ? matches[0].Id : null };
            query.HasAnyProviderId = null;
        }
        query.Name = entity.Name;
        var candidates = library.GetItemList(query);
        if (candidates.Count > 200) return entity;
        var filtered = candidates.Where(item => CompatibleId(entity.MusicBrainzId, item.ProviderIds.GetValueOrDefault(entity.Kind == "artist" ? "MusicBrainzArtist" : "MusicBrainzAlbum"))
            && Normalize(item.Name) == Normalize(entity.Name)
            && (item is not MusicAlbum album || album.AlbumArtists.Any(a => Normalize(a) == Normalize(entity.Artist)))).ToArray();
        return entity with { ItemId = filtered.Length == 1 ? filtered[0].Id : null };
    }

    public LocalMusic Get(Guid userId, Guid itemId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var item = library.GetItemById<Audio>(itemId, userId) ?? throw new KeyNotFoundException("Track is unavailable.");
        if (data is CoordinatedUserData coordinated)
        {
            var snapshot = coordinated.GetFavouriteSnapshot(User(userId), item);
            return Map(item, snapshot.Value) with { FavouriteRevision = snapshot.Revision };
        }
        return Map(item, data.GetUserData(User(userId), item));
    }

    public IReadOnlyList<LocalMusic> GetFavourites(Guid userId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var query = Query(userId, 10001);
        query.IsFavorite = true;
        var items = library.GetItemList(query);
        if (items.Count > 10000) throw new InvalidOperationException("Favourite sync supports up to 10,000 local favourites.");
        var itemData = data.GetUserDataBatch(items, User(userId));
        ct.ThrowIfCancellationRequested();
        return items.OfType<Audio>().Select(item => Map(item, itemData.GetValueOrDefault(item.Id))).ToArray();
    }

    public void SetFavourite(Guid userId, Guid itemId, bool favourite, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var item = library.GetItemById<Audio>(itemId, userId) ?? throw new KeyNotFoundException("Track is unavailable.");
        var user = User(userId);
        var state = data.GetUserData(user, item) ?? new UserItemData { Key = item.GetUserDataKeys().First() };
        if (state.IsFavorite == favourite) return;
        state.IsFavorite = favourite;
        data.SaveUserData(user, item, state, UserDataSaveReason.UpdateUserData, ct);
    }

    public bool TrySetFavourite(Guid userId, Guid itemId, bool favourite, string expectedRevision, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var item = library.GetItemById<Audio>(itemId, userId) ?? throw new KeyNotFoundException("Track is unavailable.");
        if (data is not CoordinatedUserData coordinated) throw new InvalidOperationException("This Jellyfin host cannot coordinate favourite edits safely.");
        return coordinated.TrySetFavourite(User(userId), item, favourite, expectedRevision, ct);
    }

    public void ApplyHistoryFloor(Guid userId, Guid itemId, int playCount, DateTime? lastPlayed, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var item = library.GetItemById<Audio>(itemId, userId) ?? throw new KeyNotFoundException("Track is unavailable.");
        var user = User(userId);
        if (data is not CoordinatedUserData coordinated)
            throw new InvalidOperationException("This Jellyfin host cannot coordinate history imports safely.");
        coordinated.ApplyHistoryFloor(user, item, playCount, lastPlayed, ct);
    }

    private InternalItemsQuery Query(Guid userId, int limit)
    {
        var user = User(userId);
        var query = new InternalItemsQuery(user)
        { IncludeItemTypes = [BaseItemKind.Audio], Recursive = true, Limit = limit, EnableTotalRecordCount = false };
        library.ConfigureUserAccess(query, user);
        return query;
    }

    private User User(Guid id) => users.GetUserById(id) ?? throw new UnauthorizedAccessException("User no longer exists.");

    private static MusicMatch MatchResult(MusicTrack track, Audio[] items) => items.Length switch
    {
        1 => new(track, items[0].Id, "matched"),
        0 => new(track, null, "missing"),
        _ => new(track, null, "ambiguous"),
    };

    private static LocalMusic Map(Audio item, UserItemData? state) => new(item.Id,
        new(item.Artists.FirstOrDefault() ?? string.Empty, item.Name, item.Album,
            item.ProviderIds.GetValueOrDefault("MusicBrainzTrack")),
        state?.IsFavorite ?? false, state?.PlayCount ?? 0, state?.LastPlayedDate);

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    private static bool CompatibleId(string? requested, string? actual) => !Guid.TryParse(requested, out var expected)
        || !Guid.TryParse(actual, out var existing) || expected == existing;
    private static MusicSeed ToSeed(BaseItem item) => new(item.Id, item.Name, (item as Audio)?.Artists.FirstOrDefault(), item is Audio ? "track" : "artist");
}
