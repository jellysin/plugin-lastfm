using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Storage;
using JellySin.Plugin.Lastfm.Tasks;
using JellySin.Plugin.Lastfm.Transport;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Features;

/// <summary>Runs the favourite task every five minutes for seven days of simulated time: each run reconciles favourites, also after a day of failures.</summary>
public sealed class FeatureWorkerSoakTests
{
    private const int TicksPerDay = 24 * 12;
    private const int Days = 7;
    private const int FailingDay = 2;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryFiveMinutesOfAWeekReconcileFavouritesThroughADayOfFailures()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        f.AddLocal("Soak favourite", true);
        await f.EnableAndSyncAsync(Ct);
        var respond = f.Core.Client.Handler;
        var clock = new FakeTimeProvider(f.Core.Clock.GetUtcNow());
        using var provider = Services(f, clock);
        var runs = provider.GetRequiredService<FavouriteRuns>();
        using var task = new FavouriteSyncTask(f.Core.Accounts, runs, NullLogger<FavouriteSyncTask>.Instance);
        var before = await NextRunAsync(f, task, runs, f.Core.Client.Calls.Count, 0);
        for (var tick = 1; tick <= Days * TicksPerDay; tick++)
        {
            f.Core.Client.Handler = tick / TicksPerDay == FailingDay ? (_, _, _) => throw new IOException("Last.fm unavailable.") : respond;
            f.Core.Clock.Advance(TimeSpan.FromMinutes(5));
            clock.Advance(TimeSpan.FromMinutes(5));
            before = await NextRunAsync(f, task, runs, before, tick);
        }
    }

    /// <summary>Runs the task as Jellyfin's interval trigger does; a run that did not call Last.fm or did not end fails the test.</summary>
    private static async Task<int> NextRunAsync(FeatureFixture f, FavouriteSyncTask task, FavouriteRuns runs, int before, int tick)
    {
        await task.ExecuteAsync(Mock.Of<IProgress<double>>(), Ct);
        for (var attempt = 0; attempt < 500 && (f.Core.Client.Calls.Count == before || runs.IsRunning(f.Core.UserId)); attempt++)
            await Task.Delay(10, Ct);
        Assert.True(f.Core.Client.Calls.Count > before, $"Tick {tick} did not call Last.fm.");
        Assert.False(runs.IsRunning(f.Core.UserId), $"The run of tick {tick} did not end.");
        return f.Core.Client.Calls.Count;
    }

    private static ServiceProvider Services(FeatureFixture f, TimeProvider clock)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLastfmFeatures();
        services.AddSingleton(f.Core.Accounts);
        services.AddSingleton<IStateStore>(f.Core.Store);
        services.AddSingleton<ILastfmClient>(f.Core.Client);
        services.AddSingleton(clock);
        services.AddSingleton(f.Favourites);
        services.AddSingleton<IMusicLibrary>(f.Library);
        services.AddSingleton<IMusicWriter>(f.Library);
        services.AddSingleton<IDiscoveryLibrary>(Mock.Of<IDiscoveryLibrary>());
        services.AddSingleton(Mock.Of<ILibraryManager>());
        services.AddSingleton(Mock.Of<IPlaylistManager>());
        services.AddSingleton(Mock.Of<IUserDataManager>());
        return services.BuildServiceProvider();
    }
}
