using JellySin.Plugin.Lastfm.Api;
using JellySin.Plugin.Lastfm.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Features;

public static class FeatureRegistration
{
    public static IServiceCollection AddLastfmFeatures(this IServiceCollection services)
    {
        services.AddSingleton<MusicApi>();
        services.AddSingleton<MusicViewCache>();
        services.AddSingleton<FeatureLocks>();
        services.AddSingleton<MusicLibrary>();
        services.AddSingleton<IMusicLibrary>(s => s.GetRequiredService<MusicLibrary>());
        services.AddSingleton<IMusicWriter>(s => s.GetRequiredService<MusicLibrary>());
        services.AddSingleton<IDiscoveryLibrary>(s => s.GetRequiredService<MusicLibrary>());
        services.AddSingleton<MusicFeatureService>();
        services.AddSingleton<LibraryGeneration>();
        services.AddSingleton<ILibraryGeneration>(s => s.GetRequiredService<LibraryGeneration>());
        services.AddSingleton<LovedReuse>();
        services.AddSingleton<FavouritesService>();
        services.AddSingleton<FavouriteRuns>();
        services.AddSingleton<DiscoveryService>();
        services.AddSingleton<PlaylistService>();
        services.AddHostedService<FeatureWorker>();
        return services;
    }
}

internal sealed class FeatureWorker(AccountService accounts, FavouriteRuns runs,
    IUserDataManager userData, TimeProvider clock, ILogger<FeatureWorker> logger) : BackgroundService
{
    private readonly System.Threading.Channels.Channel<Guid> changes = System.Threading.Channels.Channel.CreateBounded<Guid>(256);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        userData.UserDataSaved += OnUserDataSaved;
        try
        {
            await ProcessChangesAsync(stoppingToken).ConfigureAwait(false);
        }
        finally { userData.UserDataSaved -= OnUserDataSaved; changes.Writer.TryComplete(); }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        userData.UserDataSaved -= OnUserDataSaved;
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs args)
    {
        if (Plugin.Instance?.Configuration.Enabled != false && args.SaveReason == UserDataSaveReason.UpdateUserData) changes.Writer.TryWrite(args.UserId);
    }

    private async Task ProcessChangesAsync(CancellationToken ct)
    {
        await foreach (var id in changes.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await Task.Delay(TimeSpan.FromSeconds(2), clock, ct).ConfigureAwait(false);
            var pending = new HashSet<Guid> { id };
            for (var count = 0; count < 256 && changes.Reader.TryRead(out var other); count++) pending.Add(other);
            foreach (var user in pending) await RefreshAsync(user, ct).ConfigureAwait(false);
        }
    }

    private async Task RefreshAsync(Guid id, CancellationToken ct)
    {
        try
        {
            var account = await accounts.GetAsync(id, ct).ConfigureAwait(false);
            if (account is null || account.NeedsReconnect) return;
            await RunFeatureAsync(id, token => runs.RunToEndAsync(id, token), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogWarning("Feature refresh for user {User} failed with {ErrorType}.", LogName.Of(id), ex.GetType().Name); }
    }

    private async Task RunFeatureAsync(Guid id, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        try { await action(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning("A feature operation for user {User} failed with {ErrorType}.", LogName.Of(id), ex.GetType().Name); }
    }
}
