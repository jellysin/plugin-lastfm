using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Storage;
using JellySin.Plugin.Lastfm.Transport;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Features;

public sealed class FeatureWorkerTests
{
    [Fact]
    public async Task UserChangesAreReconciledAndEventsDetachBeforeShutdown()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var track = f.AddLocal("Event favourite", true);
        await f.Favourites.SetEnabledAsync(f.Core.UserId, true, ct);
        EventHandler<UserDataSaveEventArgs>? handler = null;
        var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var data = new Mock<IUserDataManager>();
        data.SetupAdd(d => d.UserDataSaved += It.IsAny<EventHandler<UserDataSaveEventArgs>>())
            .Callback<EventHandler<UserDataSaveEventArgs>>(value => { handler += value; attached.TrySetResult(); });
        data.SetupRemove(d => d.UserDataSaved -= It.IsAny<EventHandler<UserDataSaveEventArgs>>())
            .Callback<EventHandler<UserDataSaveEventArgs>>(value => handler -= value);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLastfmFeatures();
        services.AddSingleton(f.Core.Accounts);
        services.AddSingleton(f.Core.Credentials);
        services.AddSingleton<IStateStore>(f.Core.Store);
        services.AddSingleton<ILastfmClient>(f.Core.Client);
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton(f.Favourites);
        services.AddSingleton<IMusicLibrary>(f.Library);
        services.AddSingleton<IMusicWriter>(f.Library);
        services.AddSingleton<IDiscoveryLibrary>(Mock.Of<IDiscoveryLibrary>());
        services.AddSingleton(Mock.Of<ILibraryManager>());
        services.AddSingleton(Mock.Of<IPlaylistManager>());
        services.AddSingleton(data.Object);
        using var provider = services.BuildServiceProvider();
        var worker = Assert.Single(provider.GetServices<IHostedService>());
        var initialCalls = f.Core.Client.Calls.Count(c => c.Method == "user.getLovedTracks");
        await worker.StartAsync(ct);
        await attached.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        for (var attempt = 0; attempt < 100 && f.Core.Client.Calls.Count(c => c.Method == "user.getLovedTracks") < initialCalls + 2; attempt++)
            await Task.Delay(10, ct);
        using (await f.Locks.EnterAsync(f.Core.UserId, ct)) f.Library.Items[track.Id] = track with { Favourite = false };
        handler?.Invoke(null, new UserDataSaveEventArgs { UserId = f.Core.UserId, SaveReason = UserDataSaveReason.UpdateUserData });
        FavouriteReview? review = null;
        for (var attempt = 0; attempt < 150; attempt++)
        {
            review = await f.Favourites.GetReviewAsync(f.Core.UserId, ct);
            if (review.Pending.Count > 0) break;
            await Task.Delay(20, ct);
        }
        Assert.NotNull(review);
        Assert.Single(review.Pending);
        await worker.StopAsync(ct);
        Assert.Null(handler);
        Assert.Single(f.Loved);
    }
}
