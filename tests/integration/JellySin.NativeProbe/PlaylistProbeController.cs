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
using static JellySin.NativeProbe.ProbeFixtures;

namespace JellySin.NativeProbe;

[Route("JellySin/NativeProbe/Playlist")]
[ApiController]
[Authorize]
[TypeFilter(typeof(FixtureAccessFilter))]
[RequestSizeLimit(16_384)]
public sealed class PlaylistProbeController(IUserDataManager data, IUserManager users, ILibraryManager library,
    IPlaylistManager playlists, PlaylistFaultState fault, IServiceProvider services) : ControllerBase
{
    /// <summary>Creates a private probe playlist from public fixture tracks, stores its pending operation and arms the fault hook.</summary>
    /// <remarks>Returns 409 while another preparation runs or an operation is already pending.</remarks>
    [HttpPost("Prepare")]
    public async Task<IActionResult> Prepare(PlaylistFixture input, CancellationToken ct)
    {
        var user = FixtureUser(users, input.UserId);
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
        var name = NamePrefix + recipeId.ToString("N");
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

    /// <summary>Runs the production playlist refresh for the fixture user's own probe playlist and reports whether the injected failure fired.</summary>
    [HttpPost("Recover")]
    public async Task<object> Recover(PlaylistRecovery input, CancellationToken ct)
    {
        var user = FixtureUser(users, input.UserId);
        var playlist = library.GetItemById<Playlist>(input.PlaylistId, user.Id);
        if (playlist is null || playlist.OwnerUserId != user.Id || playlist.IsFile
            || !playlist.Name.StartsWith(NamePrefix, StringComparison.Ordinal)) throw new UnauthorizedAccessException();
        var contract = ProductionType(data, "Features.PlaylistService");
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

    /// <summary>Writes a pending playlist operation through the production state store.</summary>
    private async Task PersistOperationAsync(Guid userId, object document, CancellationToken ct)
    {
        var operationType = ProductionType(data, "Features.PlaylistOperation");
        var operation = JsonSerializer.Deserialize(JsonSerializer.Serialize(document), operationType)!;
        var contract = ProductionType(data, "Storage.IStateStore");
        var store = services.GetRequiredService(contract);
        var write = contract.GetMethod("WriteAsync")!.MakeGenericMethod(operationType);
        await ((Task)write.Invoke(store, [userId, "feature-playlist-operation", operation, ct])!).ConfigureAwait(false);
    }

    /// <summary>Reports whether the production state store holds a pending playlist operation for the user.</summary>
    private async Task<bool> HasPendingOperationAsync(Guid userId, CancellationToken ct)
    {
        var contract = ProductionType(data, "Storage.IStateStore");
        var store = services.GetRequiredService(contract);
        var read = contract.GetMethod("ReadAsync")!.MakeGenericMethod(ProductionType(data, "Features.PlaylistOperation"));
        var task = (Task)read.Invoke(store, [userId, "feature-playlist-operation", ct])!;
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")!.GetValue(task) is not null;
    }
}

public sealed record PlaylistFixture(Guid UserId, [Required, MinLength(2), MaxLength(20)] Guid[] Items);
public sealed record PlaylistRecovery(Guid UserId, Guid PlaylistId);
