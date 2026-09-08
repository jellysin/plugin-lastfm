using System.Reflection;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace JellySin.NativeProbe;

public sealed class FixtureRegistration : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        if (Environment.GetEnvironmentVariable("JELLYSIN_NATIVE_PROBE") != "1") return;
        var state = new PlaylistFaultState();
        services.AddSingleton(state);
        var descriptor = services.LastOrDefault(entry => entry.ServiceType == typeof(IPlaylistManager));
        if (descriptor?.Lifetime != ServiceLifetime.Singleton || descriptor.ImplementationType is null
            || descriptor.ImplementationType.Assembly.GetName().Name != "Emby.Server.Implementations") return;
        services.Remove(descriptor);
        services.AddSingleton<IPlaylistManager>(provider =>
        {
            var native = (IPlaylistManager)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType);
            var proxy = DispatchProxy.Create<IPlaylistManager, PlaylistFaultProxy>();
            ((PlaylistFaultProxy)(object)proxy).Initialize(native, state);
            return proxy;
        });
        state.Configured = true;
    }
}
