using System.ComponentModel.DataAnnotations;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Playback;
using Microsoft.AspNetCore.Mvc;

namespace JellySin.Plugin.Lastfm.Api;

public sealed record UserLibraryView(Guid Id, string Name, bool IsAdminAllowed, bool IsUserExcluded, bool IsScrobbled);

public sealed record UserLibrariesResponse(string Mode, IReadOnlyList<UserLibraryView> Libraries);

public sealed record UpdateUserLibrariesRequest(IReadOnlyList<Guid>? ExcludedLibraries);

public sealed record AdminLibraryView(Guid Id, string Name);

[Route("JellySin/Lastfm/Me/Libraries")]
public sealed class UserLibrariesController(LibraryRuleService libraryRules) : PrivateController
{
    [HttpGet]
    public async Task<UserLibrariesResponse> Get(CancellationToken cancellationToken)
    {
        var config = libraryRules.GetConfiguration();
        var mode = config?.LibraryMode ?? LibraryFilterMode.All;
        var adminLibs = config?.LibraryIds ?? [];
        var userExcluded = await libraryRules.GetUserExcludedLibrariesAsync(CallerId, cancellationToken).ConfigureAwait(false);
        var userExcludedSet = new HashSet<Guid>(userExcluded);
        var adminSet = new HashSet<Guid>(adminLibs);

        var available = libraryRules.GetAvailableLibraries()
            .Select(lib =>
            {
                var isAdminAllowed = mode switch
                {
                    LibraryFilterMode.AllowList => adminSet.Contains(lib.Id),
                    LibraryFilterMode.BlockList => !adminSet.Contains(lib.Id),
                    _ => true
                };
                var isUserExcluded = userExcludedSet.Contains(lib.Id);
                var isScrobbled = isAdminAllowed && !isUserExcluded;
                return new UserLibraryView(lib.Id, lib.Name, isAdminAllowed, isUserExcluded, isScrobbled);
            }).ToArray();

        return new UserLibrariesResponse(mode.ToString(), available);
    }

    [HttpPut]
    public async Task<UserLibrariesResponse> Update(UpdateUserLibrariesRequest request, CancellationToken cancellationToken)
    {
        await libraryRules.SetUserExcludedLibrariesAsync(CallerId, request.ExcludedLibraries ?? [], cancellationToken).ConfigureAwait(false);
        return await Get(cancellationToken).ConfigureAwait(false);
    }
}

[Route("JellySin/Lastfm/Admin/Libraries")]
public sealed class AdminLibrariesController(LibraryRuleService libraryRules) : PrivateController
{
    [HttpGet]
    public IReadOnlyList<AdminLibraryView> Get()
    {
        if (!Identity.User!.HasPermission(PermissionKind.IsAdministrator)) throw new UnauthorizedAccessException();
        return libraryRules.GetAvailableLibraries().Select(lib => new AdminLibraryView(lib.Id, lib.Name)).ToArray();
    }
}
