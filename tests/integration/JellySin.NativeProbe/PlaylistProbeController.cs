using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace JellySin.NativeProbe;

[Route("JellySin/NativeProbe/Playlist")]
[ApiController]
[Authorize]
[TypeFilter(typeof(FixtureAccessFilter))]
[RequestSizeLimit(16_384)]
public sealed class PlaylistProbeController(IUserDataManager data, IUserManager users, ILibraryManager library,
    IPlaylistManager playlists, PlaylistFaultState fault, IServiceProvider services) : ControllerBase
{
    [HttpPost("Prepare")]
    public async Task<IActionResult> Prepare(PlaylistFixture input, CancellationToken ct)
    {
        var user = FixtureUser(input.UserId);
        using var preparation = fault.TryBeginPreparation();
        if (preparation is null || await HasPendingOperationAsync(user.Id, ct).ConfigureAwait(false)) return Conflict();
        if (!fault.Configured || input.Items.Length is < 2 or > 20 || input.Items.Distinct().Count() != input.Items.Length)
            throw new InvalidOperationException("Invalid fixture operation or unavailable native fault hook.");
        foreach (var id in input.Items)
        {
            var track = library.GetItemById<Audio>(id, user.Id);
            if (track is null || !track.Path.StartsWith("/media/Public/", StringComparison.Ordinal))
                throw new UnauthorizedAccessException();
        }
        var recipeId = Guid.NewGuid();
        var name = "JellySin Probe " + recipeId.ToString("N");
        var created = await playlists.CreatePlaylist(new PlaylistCreationRequest
        { Name = name, UserId = user.Id, Public = false, MediaType = MediaType.Audio, ItemIdList = input.Items }).ConfigureAwait(false);
        var playlistId = Guid.Parse(created.Id);
        var operationId = Guid.NewGuid();
        await PersistOperationAsync(user.Id, new
        {
            Recipe = new
            {
                Id = recipeId,
                Name = name,
                Source = 1,
                Period = "overall",
                Limit = input.Items.Length,
                DailyRefresh = false,
                PlaylistId = playlistId
            },
            Items = input.Items.Reverse().ToArray(),
            PlaylistId = playlistId,
            OperationId = operationId
        }, ct).ConfigureAwait(false);
        fault.Arm(user.Id, playlistId);
        return Ok(new { PlaylistId = playlistId, RecipeId = recipeId, OperationId = operationId, IntendedItems = input.Items.Reverse().ToArray() });
    }

    [HttpPost("Recover")]
    public async Task<object> Recover(PlaylistRecovery input, CancellationToken ct)
    {
        var user = FixtureUser(input.UserId);
        var playlist = library.GetItemById<Playlist>(input.PlaylistId, user.Id);
        if (playlist is null || playlist.OwnerUserId != user.Id || playlist.IsFile
            || !playlist.Name.StartsWith("JellySin Probe ", StringComparison.Ordinal)) throw new UnauthorizedAccessException();
        var contract = ProductionType("Features.PlaylistService");
        var service = services.GetRequiredService(contract);
        try
        {
            await ((Task)contract.GetMethod("RefreshDueAsync")!.Invoke(service, [user.Id, ct])!).ConfigureAwait(false);
            return new { Recovered = true, InjectedFailure = false, fault.Cleared };
        }
        catch (IOException) when (fault.Cleared)
        {
            return new { Recovered = false, InjectedFailure = true, fault.Cleared };
        }
    }

    private async Task PersistOperationAsync(Guid userId, object document, CancellationToken ct)
    {
        var operationType = ProductionType("Features.PlaylistOperation");
        var operation = JsonSerializer.Deserialize(JsonSerializer.Serialize(document), operationType)!;
        var contract = ProductionType("Storage.IStateStore");
        var store = services.GetRequiredService(contract);
        var write = contract.GetMethod("WriteAsync")!.MakeGenericMethod(operationType);
        await ((Task)write.Invoke(store, [userId, "feature-playlist-operation", operation, ct])!).ConfigureAwait(false);
    }

    private async Task<bool> HasPendingOperationAsync(Guid userId, CancellationToken ct)
    {
        var contract = ProductionType("Storage.IStateStore");
        var store = services.GetRequiredService(contract);
        var read = contract.GetMethod("ReadAsync")!.MakeGenericMethod(ProductionType("Features.PlaylistOperation"));
        var task = (Task)read.Invoke(store, [userId, "feature-playlist-operation", ct])!;
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")!.GetValue(task) is not null;
    }

    private Type ProductionType(string suffix) => data.GetType().Assembly.GetType("JellySin.Plugin.Lastfm." + suffix, throwOnError: true)!;

    private User FixtureUser(Guid id)
    {
        var user = users.GetUserById(id) ?? throw new KeyNotFoundException("Fixture user missing.");
        if (!user.Username.StartsWith("JellySin Probe ", StringComparison.Ordinal)) throw new UnauthorizedAccessException();
        return user;
    }
}

public sealed record PlaylistFixture(Guid UserId, [Required, MinLength(2), MaxLength(20)] Guid[] Items);
public sealed record PlaylistRecovery(Guid UserId, Guid PlaylistId);
