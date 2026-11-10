using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;

namespace JellySin.NativeProbe;

/// <summary>Fixture checks and production type lookup shared by the probe controllers.</summary>
internal static class ProbeFixtures
{
    public const string NamePrefix = "JellySin Probe ";

    // Jellyfin isolates plugin assemblies: obtain the actual registered service type from the injected decorator.
    public static Type ProductionType(IUserDataManager data, string suffix) => data.GetType().Assembly.GetType("JellySin.Plugin.Lastfm." + suffix, throwOnError: true)!;

    public static User FixtureUser(IUserManager users, Guid id)
    {
        var user = users.GetUserById(id) ?? throw new KeyNotFoundException("Fixture user missing.");
        if (!user.Username.StartsWith(NamePrefix, StringComparison.Ordinal)) throw new UnauthorizedAccessException();
        return user;
    }
}
