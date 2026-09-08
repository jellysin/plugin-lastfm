using System.Net;
using Jellyfin.Data.Events.Users;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Playback;
using JellySin.Plugin.Lastfm.Storage;
using JellySin.Plugin.Lastfm.Transport;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        UserDataRegistration.Decorate(serviceCollection);
        serviceCollection.AddSingleton(TimeProvider.System);
        serviceCollection.AddSingleton<JellySin.Plugin.Lastfm.Api.QuickConnectLimiter>();
        serviceCollection.AddSingleton<IStateStore>(provider => new FileStateStore(Path.Combine(provider.GetRequiredService<IApplicationPaths>().DataPath, "jellysin-lastfm", "state")));
        serviceCollection.AddSingleton(provider => new LastfmProtection(Path.Combine(provider.GetRequiredService<IApplicationPaths>().DataPath, "jellysin-lastfm", "keys")));
        serviceCollection.AddSingleton(provider => new ApplicationCredentialService(provider.GetRequiredService<IStateStore>(), provider.GetRequiredService<LastfmProtection>().Provider));
        serviceCollection.AddSingleton(provider => new AccountService(provider.GetRequiredService<IStateStore>(), provider.GetRequiredService<ILastfmClient>(),
            provider.GetRequiredService<ApplicationCredentialService>(), provider.GetRequiredService<LastfmProtection>().Provider, provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<IUserManager>()));
        serviceCollection.AddScoped<IEventConsumer<UserDeletedEventArgs>, AccountLifecycle>();
        serviceCollection.AddScoped<IEventConsumer<UserUpdatedEventArgs>, AccountLifecycle>();
        serviceCollection.AddScoped<IEventConsumer<UserLockedOutEventArgs>, AccountLifecycle>();
        serviceCollection.AddSingleton<LastfmClient>(provider => new LastfmClient(new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        })
        { Timeout = Timeout.InfiniteTimeSpan }, provider.GetRequiredService<ApplicationCredentialService>(), provider.GetRequiredService<IStateStore>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILogger<LastfmClient>>()));
        serviceCollection.AddSingleton<ILastfmClient>(provider => provider.GetRequiredService<LastfmClient>());
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<LastfmClient>());
        serviceCollection.AddSingleton<PlaybackTracker>();
        serviceCollection.AddSingleton<ScrobbleOutbox>();
        serviceCollection.AddSingleton<PlaybackService>();
        serviceCollection.AddHostedService(provider => provider.GetRequiredService<PlaybackService>());
        serviceCollection.AddHostedService<DeliveryService>();
        serviceCollection.AddLastfmFeatures();
    }
}
