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
        services.AddSingleton<FavouritesService>();
        services.AddSingleton<DiscoveryService>();
        services.AddSingleton<PlaylistService>();
        services.AddSingleton<Metadata.MetadataApi>();
        services.AddHostedService<FeatureWorker>();
        return services;
    }
}

internal sealed class FeatureWorker(AccountService accounts, FavouritesService favourites, PlaylistService playlists,
    IUserDataManager userData, TimeProvider clock, ILogger<FeatureWorker> logger) : BackgroundService
{
    private readonly System.Threading.Channels.Channel<Guid> changes = System.Threading.Channels.Channel.CreateBounded<Guid>(256);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        userData.UserDataSaved += OnUserDataSaved;
        try
        {
            await Task.WhenAll(RefreshAllAsync(stoppingToken), ProcessChangesAsync(stoppingToken)).ConfigureAwait(false);
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

    private async Task RefreshAllAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5), clock);
        do
        {
            try
            {
                if (Plugin.Instance?.Configuration.Enabled == false) continue;
                var ids = await accounts.GetAccountsAsync(ct).ConfigureAwait(false);
                foreach (var id in ids.Take(8192)) await RefreshAsync(id, true, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogWarning("Feature scheduling failed with {ErrorType}.", ex.GetType().Name); }
        }
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
    }

    private async Task ProcessChangesAsync(CancellationToken ct)
    {
        await foreach (var id in changes.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            await Task.Delay(TimeSpan.FromSeconds(2), clock, ct).ConfigureAwait(false);
            var pending = new HashSet<Guid> { id };
            for (var count = 0; count < 256 && changes.Reader.TryRead(out var other); count++) pending.Add(other);
            foreach (var user in pending) await RefreshAsync(user, false, ct).ConfigureAwait(false);
        }
    }

    private async Task RefreshAsync(Guid id, bool includePlaylists, CancellationToken ct)
    {
        try
        {
            var account = await accounts.GetAsync(id, ct).ConfigureAwait(false);
            if (account is null || account.NeedsReconnect) return;
            await RunFeatureAsync(id, token => favourites.SyncAsync(id, token), ct).ConfigureAwait(false);
            if (includePlaylists) await RunFeatureAsync(id, token => playlists.RefreshDueAsync(id, token), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogWarning("Feature refresh for {UserId} failed with {ErrorType}.", id, ex.GetType().Name); }
    }

    private async Task RunFeatureAsync(Guid id, Func<CancellationToken, Task> action, CancellationToken ct)
    {
        try { await action(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning("A feature operation for {UserId} failed with {ErrorType}.", id, ex.GetType().Name); }
    }
}
