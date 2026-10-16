using Microsoft.AspNetCore.Mvc;

namespace JellySin.Plugin.Lastfm.Api;

public sealed record UserMediaTypeView(string Type, bool IsAdminAllowed, bool IsUserExcluded, bool IsScrobbled);

public sealed record UpdateUserMediaTypesRequest(IReadOnlyList<string>? ExcludedMediaTypes);

[Route("JellySin/Lastfm/Me/MediaTypes")]
public sealed class UserMediaTypesController(Playback.LibraryRuleService libraryRules) : PrivateController
{
    [HttpGet]
    public async Task<IReadOnlyList<UserMediaTypeView>> Get(CancellationToken cancellationToken)
    {
        var config = libraryRules.GetConfiguration();
        var userExcluded = await libraryRules.GetUserExcludedMediaTypesAsync(CallerId, cancellationToken).ConfigureAwait(false);
        var excludedSet = new HashSet<string>(userExcluded, StringComparer.Ordinal);

        return Playback.LibraryRuleService.KnownMediaTypes.Select(type =>
        {
            var isAdminAllowed = Playback.LibraryRuleService.IsAdminAllowed(config, type);
            var isUserExcluded = excludedSet.Contains(type);
            var isScrobbled = isAdminAllowed && !isUserExcluded;
            return new UserMediaTypeView(type, isAdminAllowed, isUserExcluded, isScrobbled);
        }).ToArray();
    }

    [HttpPut]
    public async Task<ActionResult<IReadOnlyList<UserMediaTypeView>>> Update(
        UpdateUserMediaTypesRequest request,
        CancellationToken cancellationToken)
    {
        var requested = request.ExcludedMediaTypes ?? [];
        if (requested.Any(type => !Playback.LibraryRuleService.KnownMediaTypes.Contains(type, StringComparer.Ordinal)))
        {
            return BadRequest("Unknown media type.");
        }

        await libraryRules.SetUserExcludedMediaTypesAsync(CallerId, requested, cancellationToken).ConfigureAwait(false);
        return Ok(await Get(cancellationToken).ConfigureAwait(false));
    }
}
