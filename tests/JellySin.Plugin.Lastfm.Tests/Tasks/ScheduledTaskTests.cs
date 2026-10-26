using System.Reflection;
using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Playback;
using JellySin.Plugin.Lastfm.Tasks;
using JellySin.Plugin.Lastfm.Tests.Core;
using JellySin.Plugin.Lastfm.Tests.Features;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Tasks;

public sealed class ScheduledTaskTests
{
    [Fact]
    public void NoPeriodicTimersOrBackgroundRetryLoopsRemainInPluginAssembly()
    {
        var assembly = typeof(Plugin).Assembly;
        var types = assembly.GetTypes();

        var periodicTimerFields = types
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Where(f => f.FieldType == typeof(PeriodicTimer))
            .Select(f => $"{f.DeclaringType?.FullName}.{f.Name}")
            .ToList();

        var periodicTimerLocals = types
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Where(m => m.GetMethodBody()?.LocalVariables.Any(v => v.LocalType == typeof(PeriodicTimer)) == true)
            .Select(m => $"{m.DeclaringType?.FullName}.{m.Name}")
            .ToList();

        var allPeriodicTimerUsages = periodicTimerFields.Concat(periodicTimerLocals).Distinct().ToList();
        Assert.True(allPeriodicTimerUsages.Count == 0,
            "PeriodicTimer instances found in plugin: " + string.Join(", ", allPeriodicTimerUsages));

        var retryTicksMethod = typeof(PlaybackService).GetMethod("QueueRetryTicksAsync",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        Assert.Null(retryTicksMethod);

        var refreshAllMethod = types.FirstOrDefault(t => t.Name == "FeatureWorker")?.GetMethod("RefreshAllAsync",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
        Assert.Null(refreshAllMethod);
    }

    [Fact]
    public void FavouriteSyncTask_DefaultTriggers_AreStartupAndFiveMinuteInterval()
    {
        using var f = new FeatureFixture();
        var task = new FavouriteSyncTask(f.Core.Accounts, f.Runs, NullLogger<FavouriteSyncTask>.Instance);
        AssertDefaultTriggers(task, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task FavouriteSyncTask_RunsReconciliation()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var local = f.AddLocal("Track for Sync");
        f.Loved.Add(local.Track);
        await f.EnableAndSyncAsync(ct);
        f.Core.Client.Calls.Clear();

        var task = new FavouriteSyncTask(f.Core.Accounts, f.Runs, NullLogger<FavouriteSyncTask>.Instance);
        await task.ExecuteAsync(Mock.Of<IProgress<double>>(), ct);

        Assert.Contains(f.Core.Client.Calls, c => c.Method == "user.getLovedTracks");
    }

    [Fact]
    public async Task FavouriteSyncTask_OverlapRefused_SecondExecutionIsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var local = f.AddLocal("Held Track");
        f.Loved.Add(local.Track);
        await f.Favourites.SetEnabledAsync(f.Core.UserId, true, ct);

        var matching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        f.Library.OnMatch = _ => { matching.TrySetResult(); release.Wait(); };

        var task = new FavouriteSyncTask(f.Core.Accounts, f.Runs, NullLogger<FavouriteSyncTask>.Instance);
        var firstRun = task.ExecuteAsync(Mock.Of<IProgress<double>>(), ct);
        await matching.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        // Second run while first is executing must be a no-op that completes immediately.
        await AssertOverlapExecutionIsNoOpAsync(task, ct);

        release.Set();
        await firstRun.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }

    [Fact]
    public async Task FavouriteSyncTask_HonoursCancellation()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var task = new FavouriteSyncTask(f.Core.Accounts, f.Runs, NullLogger<FavouriteSyncTask>.Instance);
        await AssertHonoursCancellationAsync(task);
    }

    [Fact]
    public void PlaylistRefreshTask_DefaultTriggers_AreStartupAndFiveMinuteInterval()
    {
        using var f = new FeatureFixture();
        var task = CreatePlaylistRefreshTask(f);
        AssertDefaultTriggers(task, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task PlaylistRefreshTask_RunsDuePlaylistRefresh()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var (_, library) = await SetupPlaylistRecoveryAsync(f, "Recover playlist", ct);
        var (playlists, updated) = SetupPlaylistUpdate();
        var task = CreatePlaylistRefreshTask(f, library.Object, playlists.Object);

        await task.ExecuteAsync(Mock.Of<IProgress<double>>(), ct);
        await updated.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        playlists.Verify(p => p.UpdatePlaylist(It.IsAny<PlaylistUpdateRequest>()), Times.Once);
    }

    [Fact]
    public async Task PlaylistRefreshTask_OverlapRefused_SecondExecutionIsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var (_, library) = await SetupPlaylistRecoveryAsync(f, "Overlap recovery", ct);
        using var release = new ManualResetEventSlim();
        var (playlists, updating) = SetupPlaylistUpdate(() => release.Wait());
        var task = CreatePlaylistRefreshTask(f, library.Object, playlists.Object);

        var firstRun = task.ExecuteAsync(Mock.Of<IProgress<double>>(), ct);
        await updating.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        // Second run while first is in progress is a no-op.
        await AssertOverlapExecutionIsNoOpAsync(task, ct);

        release.Set();
        await firstRun.WaitAsync(TimeSpan.FromSeconds(5), ct);
        playlists.Verify(p => p.UpdatePlaylist(It.IsAny<PlaylistUpdateRequest>()), Times.Once);
    }

    [Fact]
    public async Task PlaylistRefreshTask_HonoursCancellation()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var task = CreatePlaylistRefreshTask(f);
        await AssertHonoursCancellationAsync(task);
    }

    [Fact]
    public void ScrobbleDeliveryTask_DefaultTriggers_AreStartupAndOneMinuteInterval()
    {
        using var fixture = new CoreFixture();
        using var bundle = CreateDeliveryTask(fixture);
        AssertDefaultTriggers(bundle.Task, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task ScrobbleDeliveryTask_RunsDeliveryOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new CoreFixture();
        var (_, outbox, playback) = await SetupDeliveryPlaybackAsync(fixture, ct);

        fixture.Client.Handler = (method, _, _) => method switch
        {
            "track.scrobble" => SuccessfulScrobbleResponse(),
            _ => JsonDocument.Parse("{}")
        };

        using var bundle = CreateDeliveryTask(fixture, playback, outbox);
        await bundle.Task.ExecuteAsync(Mock.Of<IProgress<double>>(), ct);

        Assert.Contains(fixture.Client.Calls, c => c.Method == "track.scrobble");
    }

    [Fact]
    public async Task ScrobbleDeliveryTask_NeverSendsBesideAnotherRun()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new CoreFixture();
        var (_, outbox, playback) = await SetupDeliveryPlaybackAsync(fixture, ct);

        var entered = 0;
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        fixture.Client.Handler = (method, _, _) =>
        {
            if (method == "track.scrobble")
            {
                Interlocked.Increment(ref entered);
                delivered.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(10));
                return SuccessfulScrobbleResponse();
            }
            return JsonDocument.Parse("{}");
        };

        using var bundle = CreateDeliveryTask(fixture, playback, outbox);

        var firstRun = bundle.Task.ExecuteAsync(Mock.Of<IProgress<double>>(), ct);
        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        // A second run waits for the first instead of sending beside it.
        var secondRun = bundle.Task.ExecuteAsync(Mock.Of<IProgress<double>>(), ct);
        await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
        Assert.False(secondRun.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref entered));

        release.Set();
        await Task.WhenAll(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal(1, Volatile.Read(ref entered));
    }

    [Fact]
    public async Task FreshScrobbleIsDeliveredWithoutATaskRun()
    {
        var ct = TestContext.Current.CancellationToken;
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var (sessions, _, _, outbox, playback, _) = CreateDeliveryServices(fixture);
        var delivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.Handler = (method, _, _) =>
        {
            if (method != "track.scrobble") return JsonDocument.Parse("{}");
            delivered.TrySetResult();
            return SuccessfulScrobbleResponse();
        };
        using var delivery = new DeliveryService(playback, outbox, fixture.Accounts, NullLogger<DeliveryService>.Instance);
        await playback.StartAsync(ct);
        await delivery.StartAsync(ct);
        // Let the start-up pass finish, so only the stored play can cause the delivery.
        await Task.Delay(TimeSpan.FromMilliseconds(100), ct);

        PlayScrobble(sessions, fixture);

        await delivered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        await delivery.StopAsync(ct);
        await playback.StopAsync(ct);
    }

    [Fact]
    public async Task ScrobbleDeliveryTask_HonoursCancellation()
    {
        using var fixture = new CoreFixture();
        using var bundle = CreateDeliveryTask(fixture);
        await AssertHonoursCancellationAsync(bundle.Task);
    }

    private sealed record DeliveryTaskBundle(DeliveryService Delivery, ScrobbleDeliveryTask Task) : IDisposable
    {
        public void Dispose() => Delivery.Dispose();
    }

    private static DeliveryTaskBundle CreateDeliveryTask(
        CoreFixture fixture,
        PlaybackService playback,
        ScrobbleOutbox outbox)
    {
        var delivery = new DeliveryService(playback, outbox, fixture.Accounts, NullLogger<DeliveryService>.Instance);
        var task = new ScrobbleDeliveryTask(playback, delivery, NullLogger<ScrobbleDeliveryTask>.Instance);
        return new DeliveryTaskBundle(delivery, task);
    }

    private static DeliveryTaskBundle CreateDeliveryTask(CoreFixture fixture)
    {
        var (_, _, _, outbox, playback, _) = CreateDeliveryServices(fixture);
        return CreateDeliveryTask(fixture, playback, outbox);
    }

    private static async Task<(Mock<ISessionManager> Sessions, ScrobbleOutbox Outbox, PlaybackService Playback)> SetupDeliveryPlaybackAsync(
        CoreFixture fixture,
        CancellationToken ct)
    {
        await fixture.ConnectAsync();
        var (sessions, _, _, outbox, playback, _) = CreateDeliveryServices(fixture);
        await playback.StartAsync(ct);
        PlayScrobble(sessions, fixture);
        await playback.StopAsync(ct);
        return (sessions, outbox, playback);
    }

    private static PlaylistRefreshTask CreatePlaylistRefreshTask(
        FeatureFixture fixture,
        ILibraryManager? libraryManager = null,
        IPlaylistManager? playlistManager = null)
    {
        var discovery = new DiscoveryService(fixture.Api, fixture.Library, Mock.Of<IDiscoveryLibrary>(), fixture.Core.Accounts);
        var playlistService = new PlaylistService(
            fixture.Api,
            discovery,
            fixture.Library,
            libraryManager ?? Mock.Of<ILibraryManager>(),
            playlistManager ?? Mock.Of<IPlaylistManager>(),
            fixture.Core.Store,
            fixture.Locks,
            fixture.Core.Clock,
            fixture.Core.Accounts);
        return new PlaylistRefreshTask(fixture.Core.Accounts, playlistService, NullLogger<PlaylistRefreshTask>.Instance);
    }

    private static async Task<(Playlist Playlist, Mock<ILibraryManager> Library)> SetupPlaylistRecoveryAsync(
        FeatureFixture fixture,
        string trackTitle,
        CancellationToken ct)
    {
        var local = fixture.AddLocal(trackTitle);
        var playlist = new Playlist { Id = Guid.NewGuid(), OwnerUserId = fixture.Core.UserId, Path = "" };
        await fixture.Core.Store.WriteAsync(
            fixture.Core.UserId,
            "feature-playlist-operation",
            new PlaylistOperation(new(Guid.NewGuid(), "Recovery", PlaylistSource.Top), [local.Id], playlist.Id, Guid.NewGuid()),
            ct);

        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemById<Playlist>(playlist.Id, fixture.Core.UserId)).Returns(playlist);
        return (playlist, library);
    }

    private static (Mock<IPlaylistManager> Playlists, TaskCompletionSource Signal) SetupPlaylistUpdate(Action? onUpdate = null)
    {
        var playlists = new Mock<IPlaylistManager>();
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        playlists.Setup(p => p.UpdatePlaylist(It.IsAny<PlaylistUpdateRequest>()))
            .Returns(() =>
            {
                signal.TrySetResult();
                onUpdate?.Invoke();
                return Task.CompletedTask;
            });
        return (playlists, signal);
    }

    private static void AssertDefaultTriggers(IScheduledTask task, TimeSpan interval)
    {
        var triggers = task.GetDefaultTriggers().ToList();
        Assert.Equal([TaskTriggerInfoType.StartupTrigger, TaskTriggerInfoType.IntervalTrigger], triggers.Select(trigger => trigger.Type));
        Assert.Equal(interval.Ticks, triggers[1].IntervalTicks);
    }

    private static async Task AssertHonoursCancellationAsync(IScheduledTask task)
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            task.ExecuteAsync(Mock.Of<IProgress<double>>(), cts.Token));
    }

    private static async Task AssertOverlapExecutionIsNoOpAsync(IScheduledTask task, CancellationToken ct)
    {
        var secondRun = task.ExecuteAsync(Mock.Of<IProgress<double>>(), ct);
        await secondRun.WaitAsync(TimeSpan.FromMilliseconds(500), ct);
        Assert.True(secondRun.IsCompletedSuccessfully);
    }

    private static JsonDocument SuccessfulScrobbleResponse() =>
        JsonDocument.Parse("{\"scrobbles\":{\"scrobble\":{\"ignoredMessage\":{\"code\":\"0\"}}}}");

    private static (Mock<ISessionManager> Sessions, Mock<IPluginManager> Plugins, PlaybackTracker Tracker, ScrobbleOutbox Outbox, PlaybackService Playback, FakeLogger<DeliveryService> Logger)
        CreateDeliveryServices(CoreFixture fixture)
    {
        var sessions = new Mock<ISessionManager>();
        var plugins = new Mock<IPluginManager>();
        plugins.SetupGet(value => value.Plugins).Returns([]);
        var tracker = new PlaybackTracker(fixture.Clock);
        var outbox = new ScrobbleOutbox(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock);
        var playback = new PlaybackService(sessions.Object, plugins.Object, tracker, outbox, fixture.Accounts, fixture.LibraryRules, fixture.Clock, NullLogger<PlaybackService>.Instance);
        var logger = new FakeLogger<DeliveryService>();
        return (sessions, plugins, tracker, outbox, playback, logger);
    }

    private static void PlayScrobble(Mock<ISessionManager> sessions, CoreFixture fixture, string sessionId = "session-1")
    {
        var args = new PlaybackProgressEventArgs
        {
            Item = new Audio
            {
                Id = Guid.NewGuid(),
                Name = "Track",
                Artists = ["Artist"],
                Album = "Album",
                AlbumArtists = [],
                RunTimeTicks = TimeSpan.FromSeconds(120).Ticks
            },
            Users = [new User("User", "auth", "password") { Id = fixture.UserId }],
            PlaySessionId = sessionId,
            PlaybackPositionTicks = 0
        };
        sessions.Raise(value => value.PlaybackStart += null, args);
        fixture.Clock.Advance(30);
        args.PlaybackPositionTicks = TimeSpan.FromSeconds(30).Ticks;
        sessions.Raise(value => value.PlaybackProgress += null, args);
        fixture.Clock.Advance(30);
        args.PlaybackPositionTicks = TimeSpan.FromSeconds(60).Ticks;
        sessions.Raise(value => value.PlaybackProgress += null, args);
        sessions.Raise(value => value.PlaybackStopped += null, new PlaybackStopEventArgs
        {
            Item = args.Item,
            Users = args.Users,
            PlaySessionId = args.PlaySessionId,
            PlaybackPositionTicks = args.PlaybackPositionTicks
        });
    }
}
