using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace JellySin.NativeProbe;

/// <summary>Runs production library matching against the scanned fixture library, with no Last.fm calls.</summary>
[Route("JellySin/NativeProbe/Library")]
[ApiController]
[Authorize]
[TypeFilter(typeof(FixtureAccessFilter))]
[RequestSizeLimit(16_384)]
public sealed class LibraryProbeController(IUserDataManager data, IUserManager users, IServiceProvider services) : ControllerBase
{
    [HttpPost("Match")]
    public object Match(LibraryFixture input, CancellationToken ct)
    {
        var user = FixtureUser(input.UserId);
        var tracks = input.Tracks.Select(track => Invoke("Features.IMusicLibrary", "Match", "Features.MusicTrack", user.Id, track, ct))
            .Select(match => new { Status = match.GetProperty("Status").GetString(), ItemId = match.GetProperty("ItemId").GetString() }).ToArray();
        var entities = input.Entities.Select(entity => Invoke("Features.IDiscoveryLibrary", "MatchEntity", "Features.DiscoveryEntity", user.Id, entity, ct))
            .Select(match => new { ItemId = match.GetProperty("ItemId").GetString() }).ToArray();
        return new { Tracks = tracks, Entities = entities };
    }

    private JsonElement Invoke(string contractName, string method, string argumentName, Guid userId, object argument, CancellationToken ct)
    {
        var contract = ProductionType(contractName);
        var value = JsonSerializer.Deserialize(JsonSerializer.Serialize(argument), ProductionType(argumentName))!;
        return JsonSerializer.SerializeToElement(contract.GetMethod(method)!.Invoke(services.GetRequiredService(contract), [userId, value, ct]));
    }

    private Type ProductionType(string suffix) => data.GetType().Assembly.GetType("JellySin.Plugin.Lastfm." + suffix, throwOnError: true)!;

    private User FixtureUser(Guid id)
    {
        var user = users.GetUserById(id) ?? throw new KeyNotFoundException("Fixture user missing.");
        if (!user.Username.StartsWith("JellySin Probe ", StringComparison.Ordinal)) throw new UnauthorizedAccessException();
        return user;
    }
}

public sealed record LibraryFixture(Guid UserId, [Required, MaxLength(40)] TrackFixture[] Tracks, [Required, MaxLength(40)] EntityFixture[] Entities);
public sealed record TrackFixture([Required, MaxLength(200)] string Artist, [Required, MaxLength(200)] string Title,
    [MaxLength(200)] string? Album, [MaxLength(64)] string? MusicBrainzId);
public sealed record EntityFixture([Required, RegularExpression("artist|album")] string Kind, [Required, MaxLength(200)] string Name,
    [MaxLength(200)] string? Artist, [MaxLength(64)] string? MusicBrainzId);
