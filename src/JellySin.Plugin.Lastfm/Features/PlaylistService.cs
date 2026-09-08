using Jellyfin.Data.Enums;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;

namespace JellySin.Plugin.Lastfm.Features;

public sealed record PlaylistOperation(PlaylistRecipe Recipe, Guid[] Items, Guid? PlaylistId);

public sealed class PlaylistService(MusicApi api, DiscoveryService discovery, IMusicLibrary music, ILibraryManager library,
    IPlaylistManager playlists, IStateStore store, FeatureLocks locks, TimeProvider clock, AccountService accounts)
{
    private const string RecipesKey = "feature-playlists";
    private const string OperationKey = "feature-playlist-operation";

    public async Task<IReadOnlyList<PlaylistRecipe>> GetRecipesAsync(Guid userId, CancellationToken ct)
    {
        var recipes = await store.ReadAsync<List<PlaylistRecipe>>(userId, RecipesKey, ct).ConfigureAwait(false) ?? [];
        var pending = await store.ReadAsync<PlaylistOperation>(userId, OperationKey, ct).ConfigureAwait(false);
        if (pending is not null && recipes.All(r => r.Id != pending.Recipe.Id))
            recipes.Add(pending.Recipe with { PlaylistId = pending.PlaylistId });
        return recipes;
    }

    public async Task<PlaylistRecipe> GenerateAsync(Guid userId, PlaylistRecipe recipe, CancellationToken ct)
    {
        using var accountOperation = accounts.LinkOperation(userId, ct);
        ct = accountOperation.Token;
        Validate(recipe);
        using var lease = await locks.EnterAsync(userId, ct).ConfigureAwait(false);
        await RecoverAsync(userId, ct).ConfigureAwait(false);
        var current = await GetRecipesAsync(userId, ct).ConfigureAwait(false);
        var previous = current.SingleOrDefault(r => r.Id == recipe.Id);
        // Retrying creation after a lost HTTP response refreshes the same recipe instead of creating duplicates.
        if (recipe.Id == Guid.Empty) previous = current.FirstOrDefault(r => SameRecipe(r, recipe));
        if (previous is null && recipe.Id != Guid.Empty) throw new KeyNotFoundException("Playlist recipe is not owned by this account.");
        if (previous is null && current.Count >= 20) throw new InvalidOperationException("An account can manage at most 20 playlist recipes.");
        recipe = recipe with { Id = previous?.Id ?? Guid.NewGuid(), PlaylistId = previous?.PlaylistId, UpdatedAt = null };
        var desired = await ResolveAsync(userId, recipe, ct).ConfigureAwait(false);
        if (desired.Length == 0) throw new InvalidOperationException("No accessible local tracks match this recipe; the existing playlist was preserved.");
        var operation = new PlaylistOperation(recipe, desired, recipe.PlaylistId);
        await store.WriteAsync(userId, OperationKey, operation, ct).ConfigureAwait(false);
        return await ExecuteAsync(userId, operation, ct).ConfigureAwait(false);
    }

    public async Task DeleteRecipeAsync(Guid userId, Guid recipeId, CancellationToken ct)
    {
        using var accountOperation = accounts.LinkOperation(userId, ct);
        ct = accountOperation.Token;
        using var lease = await locks.EnterAsync(userId, ct).ConfigureAwait(false);
        await RecoverAsync(userId, ct).ConfigureAwait(false);
        var recipes = (await GetRecipesAsync(userId, ct).ConfigureAwait(false)).Where(r => r.Id != recipeId).ToList();
        await store.WriteAsync(userId, RecipesKey, recipes, ct).ConfigureAwait(false);
    }

    public async Task RefreshDueAsync(Guid userId, CancellationToken ct)
    {
        using var accountOperation = accounts.LinkOperation(userId, ct);
        ct = accountOperation.Token;
        using (await locks.EnterAsync(userId, ct).ConfigureAwait(false)) await RecoverAsync(userId, ct).ConfigureAwait(false);
        var recipes = await GetRecipesAsync(userId, ct).ConfigureAwait(false);
        foreach (var recipe in recipes.Where(r => r.DailyRefresh && (r.UpdatedAt is null || r.UpdatedAt <= clock.GetUtcNow().AddDays(-1))))
            await GenerateAsync(userId, recipe, ct).ConfigureAwait(false);
    }

    private async Task<Guid[]> ResolveAsync(Guid userId, PlaylistRecipe recipe, CancellationToken ct)
    {
        if (recipe.Source is PlaylistSource.Similar or PlaylistSource.Discovery)
            return (await discovery.GetAsync(userId, recipe.SeedItemId, ct).ConfigureAwait(false)).Local
                .Select(m => m.ItemId!.Value).Distinct().Take(recipe.Limit).ToArray();
        var page = recipe.Source == PlaylistSource.Loved
            ? await api.GetAllLovedAsync(userId, ct).ConfigureAwait(false)
            : await api.GetPageAsync(userId, "user.getTopTracks", 1, recipe.Period, ct).ConfigureAwait(false);
        if (recipe.Source == PlaylistSource.Loved && !page.Complete)
            throw new InvalidOperationException("Loved tracks were incomplete; the existing playlist was preserved.");
        return page.Tracks.Select(t => music.Match(userId, t, ct)).Where(m => m.ItemId.HasValue)
            .Select(m => m.ItemId!.Value).Distinct().Take(recipe.Limit).ToArray();
    }

    private async Task RecoverAsync(Guid userId, CancellationToken ct)
    {
        var pending = await store.ReadAsync<PlaylistOperation>(userId, OperationKey, ct).ConfigureAwait(false);
        if (pending is not null) await ExecuteAsync(userId, pending, ct).ConfigureAwait(false);
    }

    private async Task<PlaylistRecipe> ExecuteAsync(Guid userId, PlaylistOperation operation, CancellationToken ct)
    {
        foreach (var item in operation.Items) music.Get(userId, item, ct);
        var id = operation.PlaylistId;
        if (id is null)
        {
            var marker = "JellySin " + operation.Recipe.Id.ToString("N");
            var matches = library.GetItemList(new InternalItemsQuery
            { IncludeItemTypes = [BaseItemKind.Playlist], Name = marker, Recursive = true, Limit = 100 })
                .OfType<Playlist>().Where(p => p.OwnerUserId == userId && p.Name == marker).ToArray();
            if (matches.Length > 1) throw new InvalidOperationException("Multiple interrupted playlist creations need manual review.");
            ct.ThrowIfCancellationRequested();
            id = matches.SingleOrDefault()?.Id;
            if (id is null)
            {
                var created = await playlists.CreatePlaylist(new PlaylistCreationRequest
                { Name = marker, UserId = userId, Public = false, MediaType = MediaType.Audio, ItemIdList = operation.Items }).ConfigureAwait(false);
                id = Guid.Parse(created.Id);
            }
            operation = operation with { PlaylistId = id };
            await store.WriteAsync(userId, OperationKey, operation, ct).ConfigureAwait(false);
        }
        var playlist = library.GetItemById<Playlist>(id.Value, userId);
        if (playlist is null || playlist.OwnerUserId != userId || playlist.IsFile)
            throw new UnauthorizedAccessException("The generated playlist is unavailable or no longer owned by this account.");
        ct.ThrowIfCancellationRequested();
        // The host clears then adds: the durable operation remains until all steps complete, so retry restores the full intended list.
        await playlists.UpdatePlaylist(new PlaylistUpdateRequest
        { Id = id.Value, UserId = userId, Name = operation.Recipe.Name, Ids = operation.Items, Public = false }).ConfigureAwait(false);
        var result = operation.Recipe with { PlaylistId = id, UpdatedAt = clock.GetUtcNow() };
        var recipes = (await GetRecipesAsync(userId, ct).ConfigureAwait(false)).Where(r => r.Id != result.Id).Append(result).ToList();
        await store.WriteAsync(userId, RecipesKey, recipes, ct).ConfigureAwait(false);
        await store.DeleteAsync(userId, OperationKey, ct).ConfigureAwait(false);
        return result;
    }

    private static void Validate(PlaylistRecipe recipe)
    {
        if (string.IsNullOrWhiteSpace(recipe.Name) || recipe.Name.Length > 100 || recipe.Name.Any(char.IsControl))
            throw new ArgumentException("Playlist name must contain between 1 and 100 printable characters.", nameof(recipe));
        if (!Enum.IsDefined(recipe.Source) || !MusicApi.Periods.Contains(recipe.Period, StringComparer.Ordinal)
            || recipe.Limit is < 1 or > 200 || recipe.Source == PlaylistSource.Similar && recipe.SeedItemId is null)
            throw new ArgumentException("Invalid playlist recipe.", nameof(recipe));
    }

    private static bool SameRecipe(PlaylistRecipe left, PlaylistRecipe right) => left.Name == right.Name && left.Source == right.Source
        && left.Period == right.Period && left.SeedItemId == right.SeedItemId && left.Limit == right.Limit && left.DailyRefresh == right.DailyRefresh;
}
