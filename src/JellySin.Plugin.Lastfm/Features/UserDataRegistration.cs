using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.DependencyInjection;

namespace JellySin.Plugin.Lastfm.Features;

internal static class UserDataRegistration
{
    public static void Decorate(IServiceCollection services)
    {
        var descriptor = services.LastOrDefault(service => service.ServiceType == typeof(IUserDataManager));
        // Only Jellyfin 12's known concrete singleton has verified detached-snapshot semantics.
        // Unknown factories/instances remain owned by their original registration.
        if (descriptor?.Lifetime != ServiceLifetime.Singleton || descriptor.ImplementationType?.FullName != "Emby.Server.Implementations.Library.UserDataManager"
            || descriptor.ImplementationType.Assembly.GetName().Name != "Emby.Server.Implementations") return;
        services.Remove(descriptor);
        services.AddSingleton<IUserDataManager>(provider =>
        {
            var original = CreateOriginal(provider, descriptor);
            return new CoordinatedUserData(original, (user, item) =>
            {
                // Resolve lazily: LibraryManager itself depends on IUserDataManager.
                var fresh = provider.GetRequiredService<ILibraryManager>().GetItemList(new InternalItemsQuery(user)
                { ItemIds = [item.Id], Limit = 1, DtoOptions = new DtoOptions(false) { EnableUserData = true } }).SingleOrDefault()
                    ?? throw new KeyNotFoundException("The music item no longer exists or is inaccessible.");
                return original.GetUserData(user, fresh);
            });
        });
    }

    private static IUserDataManager CreateOriginal(IServiceProvider provider, ServiceDescriptor descriptor) =>
        (IUserDataManager)ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
}
