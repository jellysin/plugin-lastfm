using System;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Lastfm.Models;

namespace Jellyfin.Plugin.Lastfm.Utils;

public static class UserHelpers
{
    public static LastfmUser? GetUser(User? user) => user is null ? null : GetUser(user.Id);

    public static LastfmUser? GetUser(Guid userId)
        => Plugin.Instance?.PluginConfiguration.LastfmUsers?.FirstOrDefault(user => user is not null && user.MediaBrowserUserId == userId);

    public static LastfmUser? GetUser(string? userGuid)
        => Guid.TryParse(userGuid, out var id) ? GetUser(id) : null;
}
