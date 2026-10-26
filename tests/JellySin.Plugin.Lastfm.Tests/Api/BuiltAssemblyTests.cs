using System.Reflection;
using System.Runtime.Loader;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Api;

/// <summary>Reads the plugin assembly from the build output that is packaged, not the copy this test run loads.</summary>
public sealed class BuiltAssemblyTests
{
    private const string Prefix = "JellySin.Plugin.Lastfm.";
    private static readonly Lazy<Assembly> Built = new(Load);

    // Every type of the plugin with the Jellyfin interfaces and base classes it has. The server finds a provider
    // by such an interface alone, so a type the record does not name is a feature nobody decided to ship.
    private static readonly Dictionary<string, string[]> HostTypes = new(StringComparer.Ordinal)
    {
        ["Configuration.AccountLifecycle"] = ["MediaBrowser.Controller.Events.IEventConsumer`1"],
        ["Configuration.PluginConfiguration"] = ["MediaBrowser.Model.Plugins.BasePluginConfiguration"],
        ["Features.CoordinatedUserData"] = ["MediaBrowser.Controller.Library.IUserDataManager"],
        ["Metadata.LastfmAlbumProvider"] =
        [
            "MediaBrowser.Controller.Providers.IMetadataProvider", "MediaBrowser.Controller.Providers.IMetadataProvider`1",
            "MediaBrowser.Controller.Providers.IRemoteMetadataProvider", "MediaBrowser.Controller.Providers.IRemoteMetadataProvider`2",
            "MediaBrowser.Controller.Providers.IRemoteSearchProvider", "MediaBrowser.Controller.Providers.IRemoteSearchProvider`1",
        ],
        ["Metadata.LastfmArtistProvider"] =
        [
            "MediaBrowser.Controller.Providers.IMetadataProvider", "MediaBrowser.Controller.Providers.IMetadataProvider`1",
            "MediaBrowser.Controller.Providers.IRemoteMetadataProvider", "MediaBrowser.Controller.Providers.IRemoteMetadataProvider`2",
            "MediaBrowser.Controller.Providers.IRemoteSearchProvider", "MediaBrowser.Controller.Providers.IRemoteSearchProvider`1",
        ],
        ["Metadata.LastfmExternalUrlProvider"] = ["MediaBrowser.Controller.Providers.IExternalUrlProvider"],
        ["Plugin"] =
        [
            "MediaBrowser.Common.Plugins.BasePlugin", "MediaBrowser.Common.Plugins.BasePlugin`1", "MediaBrowser.Common.Plugins.IHasPluginConfiguration",
            "MediaBrowser.Common.Plugins.IPlugin", "MediaBrowser.Common.Plugins.IPluginAssembly", "MediaBrowser.Model.Plugins.IHasWebPages",
        ],
        ["PluginServiceRegistrator"] = ["MediaBrowser.Controller.Plugins.IPluginServiceRegistrator"],
        // Abstract: the server's task scan skips it and finds only the two tasks built on it.
        ["Tasks.AccountTask"] = ["MediaBrowser.Model.Tasks.IScheduledTask"],
        ["Tasks.FavouriteSyncTask"] = ["MediaBrowser.Model.Tasks.IScheduledTask"],
        ["Tasks.LibraryScanRematchTask"] = ["MediaBrowser.Controller.Library.ILibraryPostScanTask"],
        ["Tasks.PlaylistRefreshTask"] = ["MediaBrowser.Model.Tasks.IScheduledTask"],
        ["Tasks.ScrobbleDeliveryTask"] = ["MediaBrowser.Model.Tasks.IScheduledTask"],
    };

    // The services the plugin adds to the server, each with the number of its registrations.
    private static readonly string[] Services =
    [
        "JellySin.Plugin.Lastfm.Api.QuickConnectLimiter x1",
        "JellySin.Plugin.Lastfm.Api.ReturnLogLimiter x1",
        "JellySin.Plugin.Lastfm.Api.ServerAddress x1",
        "JellySin.Plugin.Lastfm.Configuration.AccountService x1",
        "JellySin.Plugin.Lastfm.Configuration.ApplicationCredentialService x1",
        "JellySin.Plugin.Lastfm.Configuration.LastfmProtection x1",
        "JellySin.Plugin.Lastfm.Features.DiscoveryService x1",
        "JellySin.Plugin.Lastfm.Features.FavouriteRuns x1",
        "JellySin.Plugin.Lastfm.Features.FavouritesService x1",
        "JellySin.Plugin.Lastfm.Features.FeatureLocks x1",
        "JellySin.Plugin.Lastfm.Features.IDiscoveryLibrary x1",
        "JellySin.Plugin.Lastfm.Features.ILibraryGeneration x1",
        "JellySin.Plugin.Lastfm.Features.IMusicLibrary x1",
        "JellySin.Plugin.Lastfm.Features.IMusicWriter x1",
        "JellySin.Plugin.Lastfm.Features.LibraryGeneration x1",
        "JellySin.Plugin.Lastfm.Features.LovedReuse x1",
        "JellySin.Plugin.Lastfm.Features.MusicApi x1",
        "JellySin.Plugin.Lastfm.Features.MusicFeatureService x1",
        "JellySin.Plugin.Lastfm.Features.MusicLibrary x1",
        "JellySin.Plugin.Lastfm.Features.MusicViewCache x1",
        "JellySin.Plugin.Lastfm.Features.PlaylistService x1",
        "JellySin.Plugin.Lastfm.Playback.DeliveryService x1",
        "JellySin.Plugin.Lastfm.Playback.LibraryRuleService x1",
        "JellySin.Plugin.Lastfm.Playback.PlaybackService x1",
        "JellySin.Plugin.Lastfm.Playback.PlaybackTracker x1",
        "JellySin.Plugin.Lastfm.Playback.ScrobbleOutbox x1",
        "JellySin.Plugin.Lastfm.Storage.IStateStore x1",
        "JellySin.Plugin.Lastfm.Transport.ILastfmClient x1",
        "JellySin.Plugin.Lastfm.Transport.LastfmClient x1",
        "MediaBrowser.Controller.Events.IEventConsumer<UserDeletedEventArgs> x1",
        "MediaBrowser.Controller.Events.IEventConsumer<UserLockedOutEventArgs> x1",
        "MediaBrowser.Controller.Events.IEventConsumer<UserUpdatedEventArgs> x1",
        "Microsoft.AspNetCore.Hosting.IStartupFilter x1",
        "Microsoft.Extensions.Hosting.IHostedService x5",
        "System.TimeProvider x1",
    ];

    [Fact]
    public void BuiltPluginHasOnlyTheRecordedJellyfinTypes()
    {
        var found = HostSurface(Built.Value).ToDictionary(entry => entry.Key[Prefix.Length..], entry => entry.Value);
        Same(Lines(HostTypes), Lines(found));
    }

    [Fact]
    public void BuiltPluginOffersNoProviderToTheServer()
    {
        var providers = Providers(HostSurface(Built.Value));
        Assert.Equal(
            [
                "JellySin.Plugin.Lastfm.Metadata.LastfmAlbumProvider: MediaBrowser.Controller.Providers.IMetadataProvider, MediaBrowser.Controller.Providers.IMetadataProvider`1, MediaBrowser.Controller.Providers.IRemoteMetadataProvider, MediaBrowser.Controller.Providers.IRemoteMetadataProvider`2, MediaBrowser.Controller.Providers.IRemoteSearchProvider, MediaBrowser.Controller.Providers.IRemoteSearchProvider`1",
                "JellySin.Plugin.Lastfm.Metadata.LastfmArtistProvider: MediaBrowser.Controller.Providers.IMetadataProvider, MediaBrowser.Controller.Providers.IMetadataProvider`1, MediaBrowser.Controller.Providers.IRemoteMetadataProvider, MediaBrowser.Controller.Providers.IRemoteMetadataProvider`2, MediaBrowser.Controller.Providers.IRemoteSearchProvider, MediaBrowser.Controller.Providers.IRemoteSearchProvider`1",
                "JellySin.Plugin.Lastfm.Metadata.LastfmExternalUrlProvider: MediaBrowser.Controller.Providers.IExternalUrlProvider",
            ],
            providers);
    }

    [Fact]
    public void BuiltPluginRegistersOnlyTheRecordedServices()
    {
        var registrator = Assert.IsAssignableFrom<IPluginServiceRegistrator>(
            Activator.CreateInstance(Built.Value.GetType(Prefix + "PluginServiceRegistrator", true)!));
        var services = new ServiceCollection();
        registrator.RegisterServices(services, Mock.Of<IServerApplicationHost>());
        Same(Services, services.GroupBy(service => Describe(service.ServiceType), StringComparer.Ordinal).Select(group => $"{group.Key} x{group.Count()}"));
    }

    [Fact]
    public void ProviderIsFoundDirectlyAndThroughABaseClass()
    {
        var planted = Providers(HostSurface(typeof(BuiltAssemblyTests).Assembly));
        Assert.Equal(
            [
                typeof(InheritedLinks).FullName + ": MediaBrowser.Controller.Providers.IExternalUrlProvider",
                typeof(PlantedLinks).FullName + ": MediaBrowser.Controller.Providers.IExternalUrlProvider",
            ],
            planted);
    }

    private static void Same(IEnumerable<string> recorded, IEnumerable<string> found)
    {
        var (expected, actual) = (recorded.ToArray(), found.ToArray());
        var differences = actual.Except(expected, StringComparer.Ordinal).Select(line => "not recorded: " + line)
            .Concat(expected.Except(actual, StringComparer.Ordinal).Select(line => "recorded, not in the build: " + line)).ToArray();
        Assert.True(differences.Length == 0, string.Join(Environment.NewLine, differences));
    }

    private static Assembly Load()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var root = output;
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "global.json"))) root = root.Parent;
        Assert.NotNull(root);
        var path = Path.Combine(root.FullName, "src", "JellySin.Plugin.Lastfm", "bin", output.Parent!.Name, output.Name, "JellySin.Plugin.Lastfm.dll");
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        return new AssemblyLoadContext("built plugin").LoadFromStream(stream);
    }

    private static SortedDictionary<string, string[]> HostSurface(Assembly assembly)
    {
        var surface = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var type in assembly.GetTypes().Where(type => !type.IsInterface))
        {
            var host = Ancestors(type).Concat(type.GetInterfaces()).Where(IsJellyfin).Select(Definition).Distinct().Order(StringComparer.Ordinal).ToArray();
            if (host.Length > 0) surface[type.FullName!] = host;
        }
        return surface;
    }

    private static IEnumerable<Type> Ancestors(Type type)
    {
        for (var ancestor = type.BaseType; ancestor is not null; ancestor = ancestor.BaseType) yield return ancestor;
    }

    private static bool IsJellyfin(Type type)
    {
        var name = type.Assembly.GetName().Name ?? string.Empty;
        return name.StartsWith("MediaBrowser.", StringComparison.Ordinal) || name.StartsWith("Jellyfin.", StringComparison.Ordinal)
            || name.StartsWith("Emby.", StringComparison.Ordinal);
    }

    private static string Definition(Type type) => (type.IsGenericType ? type.GetGenericTypeDefinition() : type).FullName!;

    private static string Describe(Type type) => type.IsGenericType
        ? $"{type.Namespace}.{type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)]}<{string.Join(", ", type.GetGenericArguments().Select(argument => argument.Name))}>"
        : type.FullName!;

    private static string[] Lines(IEnumerable<KeyValuePair<string, string[]>> surface) =>
        surface.OrderBy(entry => entry.Key, StringComparer.Ordinal).Select(entry => entry.Key + ": " + string.Join(", ", entry.Value)).ToArray();

    private static string[] Providers(SortedDictionary<string, string[]> surface) => Lines(surface
        .Select(entry => KeyValuePair.Create(entry.Key, entry.Value.Where(name => name[(name.LastIndexOf('.') + 1)..].Contains("Provider", StringComparison.Ordinal)).ToArray()))
        .Where(entry => entry.Value.Length > 0));

    private class PlantedLinks : IExternalUrlProvider
    {
        public string Name => "planted";

        public IEnumerable<string> GetExternalUrls(BaseItem item) => [];
    }

    private sealed class InheritedLinks : PlantedLinks;
}
