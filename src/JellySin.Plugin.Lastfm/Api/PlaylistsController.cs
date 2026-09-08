using System.ComponentModel.DataAnnotations;
using JellySin.Plugin.Lastfm.Features;
using Microsoft.AspNetCore.Mvc;

namespace JellySin.Plugin.Lastfm.Api;

[Route("JellySin/Lastfm/Me/Playlists")]
public sealed class PlaylistsController(PlaylistService playlists) : PrivateController
{
    [HttpGet]
    public Task<IReadOnlyList<PlaylistRecipe>> Get(CancellationToken cancellationToken) =>
        playlists.GetRecipesAsync(CallerId, cancellationToken);

    [HttpGet("Pending")]
    public async Task<object?> Pending(CancellationToken cancellationToken)
    {
        var pending = await playlists.GetPendingOperationAsync(CallerId, cancellationToken).ConfigureAwait(false);
        return pending is null ? null : new
        {
            pending.OperationId,
            RecipeId = pending.Recipe.Id,
            pending.Recipe.Name,
            pending.PlaylistId,
            ItemCount = pending.Items.Length,
            Status = pending.Status ?? "Update is waiting for completion or recovery."
        };
    }

    [HttpDelete("Pending/{id:guid}")]
    public async Task<IActionResult> CancelPending(Guid id, CancellationToken cancellationToken)
    {
        await playlists.CancelPendingAsync(CallerId, id, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost]
    public Task<PlaylistRecipe> Generate(PlaylistInput input, CancellationToken cancellationToken) =>
        playlists.GenerateAsync(CallerId, new(input.Id, input.Name, input.Source, input.Period,
            input.SeedItemId, input.Limit, input.DailyRefresh), cancellationToken);

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> StopManaging(Guid id, CancellationToken cancellationToken)
    {
        await playlists.DeleteRecipeAsync(CallerId, id, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}

public sealed record PlaylistInput(
    Guid Id,
    [Required, StringLength(100, MinimumLength = 1)] string Name,
    PlaylistSource Source,
    [Required, StringLength(16)] string Period = "1month",
    Guid? SeedItemId = null,
    [Range(1, 200)] int Limit = 50,
    bool DailyRefresh = false);
