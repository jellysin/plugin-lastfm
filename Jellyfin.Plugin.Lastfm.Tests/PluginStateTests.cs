using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Lastfm.Configuration;
using Jellyfin.Plugin.Lastfm.Models;
using Jellyfin.Plugin.Lastfm.ScheduledTasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Jellyfin.Plugin.Lastfm.Tests;

[CollectionDefinition("Plugin state", DisableParallelization = true)]
public sealed class PluginStateCollection;

[Collection("Plugin state")]
public sealed class PluginStateTests
{
    [Fact]
    public async Task StartingTwiceDoesNotDuplicateSubscriptionsAndStoppingUnsubscribes()
    {
        using var factory = TestHttpClientFactory.Responding("{}");
        var sessions = new Mock<ISessionManager>();
        var userData = new Mock<IUserDataManager>();
        using var service = new ServerEntryPoint(sessions.Object, factory, NullLoggerFactory.Instance, userData.Object);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);
        sessions.VerifyAdd(value => value.PlaybackStart += It.IsAny<EventHandler<PlaybackProgressEventArgs>>(), Times.Once);
        sessions.VerifyAdd(value => value.PlaybackProgress += It.IsAny<EventHandler<PlaybackProgressEventArgs>>(), Times.Once);
        sessions.VerifyAdd(value => value.PlaybackStopped += It.IsAny<EventHandler<PlaybackStopEventArgs>>(), Times.Once);
        userData.VerifyAdd(value => value.UserDataSaved += It.IsAny<EventHandler<UserDataSaveEventArgs>>(), Times.Once);
        sessions.VerifyRemove(value => value.PlaybackStart -= It.IsAny<EventHandler<PlaybackProgressEventArgs>>(), Times.Once);
        sessions.VerifyRemove(value => value.PlaybackProgress -= It.IsAny<EventHandler<PlaybackProgressEventArgs>>(), Times.Once);
        sessions.VerifyRemove(value => value.PlaybackStopped -= It.IsAny<EventHandler<PlaybackStopEventArgs>>(), Times.Once);
        userData.VerifyRemove(value => value.UserDataSaved -= It.IsAny<EventHandler<UserDataSaveEventArgs>>(), Times.Once);
    }

    [Fact]
    public async Task DisposeWithoutStopUnsubscribesAndClearsHostedServiceInstance()
    {
        using var factory = TestHttpClientFactory.Responding("{}");
        var sessions = new Mock<ISessionManager>();
        var userData = new Mock<IUserDataManager>();
        var service = new ServerEntryPoint(sessions.Object, factory, NullLoggerFactory.Instance, userData.Object);
        await service.StartAsync(TestContext.Current.CancellationToken);
        service.Dispose();
        service.Dispose();
        sessions.VerifyRemove(value => value.PlaybackStart -= It.IsAny<EventHandler<PlaybackProgressEventArgs>>(), Times.Once);
        userData.VerifyRemove(value => value.UserDataSaved -= It.IsAny<EventHandler<UserDataSaveEventArgs>>(), Times.Once);
        ServerEntryPoint.Instance.ShouldBeNull();
    }

    [Fact]
    public async Task StartingAfterShutdownIsRejectedWithoutReattachingEvents()
    {
        using var factory = TestHttpClientFactory.Responding("{}");
        var sessions = new Mock<ISessionManager>();
        using var service = new ServerEntryPoint(sessions.Object, factory, NullLoggerFactory.Instance, Mock.Of<IUserDataManager>());
        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(TestContext.Current.CancellationToken));

        sessions.VerifyAdd(value => value.PlaybackStart += It.IsAny<EventHandler<PlaybackProgressEventArgs>>(), Times.Once);
        sessions.VerifyRemove(value => value.PlaybackStart -= It.IsAny<EventHandler<PlaybackProgressEventArgs>>(), Times.Once);
    }

    [Fact]
    public async Task ConcurrentStopsAndDisposeShareShutdownUntilInFlightWorkFinishes()
    {
        var user = ConfigureUser();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var factory = new TestHttpClientFactory(async (_, token) =>
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return TestHttpClientFactory.JsonResponse("{}");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                canceled.TrySetResult();
                await finish.Task.WaitAsync(TestContext.Current.CancellationToken);
                throw;
            }
        });
        var sessions = new Mock<ISessionManager>();
        using var service = new ServerEntryPoint(sessions.Object, factory, NullLoggerFactory.Instance, Mock.Of<IUserDataManager>());
        await service.StartAsync(TestContext.Current.CancellationToken);
        sessions.Raise(value => value.PlaybackStart += null, new PlaybackProgressEventArgs
        {
            Item = new Audio { Name = "Song", Artists = ["Artist"] },
            Users = [user]
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var firstStop = service.StopAsync(TestContext.Current.CancellationToken);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var secondStop = service.StopAsync(TestContext.Current.CancellationToken);
        service.Dispose();
        service.Dispose();
        firstStop.IsCompleted.ShouldBeFalse();
        secondStop.IsCompleted.ShouldBeFalse();
        finish.TrySetResult();
        await Task.WhenAll(firstStop, secondStop).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        sessions.VerifyRemove(value => value.PlaybackStart -= It.IsAny<EventHandler<PlaybackProgressEventArgs>>(), Times.Once);
        ServerEntryPoint.Instance.ShouldBeNull();
        factory.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task StopCancelsAndDrainsInFlightPlaybackRequests()
    {
        var user = ConfigureUser();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var factory = new TestHttpClientFactory(async (_, token) =>
        {
            entered.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return TestHttpClientFactory.JsonResponse("{}");
            }
            finally
            {
                finished.SetResult();
            }
        });
        var sessions = new Mock<ISessionManager>();
        using var service = new ServerEntryPoint(sessions.Object, factory, NullLoggerFactory.Instance, Mock.Of<IUserDataManager>());
        await service.StartAsync(TestContext.Current.CancellationToken);
        var playback = new PlaybackProgressEventArgs { Item = new Audio { Name = "Song", Artists = ["Artist"], AlbumArtists = [] }, Users = [user] };
        sessions.Raise(value => value.PlaybackStart += null, playback);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        finished.Task.IsCompletedSuccessfully.ShouldBeTrue();
        sessions.Raise(value => value.PlaybackStart += null, playback);
        factory.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task OverloadedPlaybackQueueWarnsOnceAndShutdownCancelsQueuedWorkBeforeHttp()
    {
        var user = ConfigureUser();
        var entered = 0;
        var canceled = 0;
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var factory = new TestHttpClientFactory(async (_, token) =>
        {
            if (Interlocked.Increment(ref entered) == 4)
            {
                running.TrySetResult();
            }
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return TestHttpClientFactory.JsonResponse("{}");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                Interlocked.Increment(ref canceled);
                throw;
            }
        });
        var logger = new Mock<ILogger>();
        var loggers = new Mock<ILoggerFactory>();
        loggers.Setup(value => value.CreateLogger(It.IsAny<string>())).Returns(logger.Object);
        var sessions = new Mock<ISessionManager>();
        using var service = new ServerEntryPoint(sessions.Object, factory, loggers.Object, Mock.Of<IUserDataManager>());
        await service.StartAsync(TestContext.Current.CancellationToken);
        var playback = new PlaybackProgressEventArgs { Item = new Audio { Name = "Song", Artists = ["Artist"] }, Users = [user] };
        for (var index = 0; index < 256; index++)
        {
            sessions.Raise(value => value.PlaybackStart += null, playback);
        }
        await running.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        logger.Invocations.Where(call => call.Method.Name == nameof(ILogger.Log) && (LogLevel)call.Arguments[0] == LogLevel.Warning).ShouldBeEmpty();
        for (var index = 0; index < 20; index++)
        {
            sessions.Raise(value => value.PlaybackStart += null, playback);
        }
        var warning = logger.Invocations.Where(call => call.Method.Name == nameof(ILogger.Log) && (LogLevel)call.Arguments[0] == LogLevel.Warning).ShouldHaveSingleItem();
        warning.Arguments[2].ToString()!.ShouldNotContain("fake-session");

        await service.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        entered.ShouldBe(4);
        canceled.ShouldBe(4);
        factory.Requests.Count.ShouldBe(4);
        logger.Invocations.Where(call => call.Method.Name == nameof(ILogger.Log) && (LogLevel)call.Arguments[0] >= LogLevel.Error).ShouldBeEmpty();
        sessions.Raise(value => value.PlaybackStart += null, playback);
        factory.Requests.Count.ShouldBe(4);
    }

    [Fact]
    public async Task PlaybackQueueLimitsConcurrentHttpAndContinuesAfterRequestsFinish()
    {
        var user = ConfigureUser();
        using var responses = new SemaphoreSlim(0);
        var active = 0;
        var peak = 0;
        var entered = 0;
        var completed = 0;
        var counterLock = new object();
        var firstBatch = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var laterRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var factory = new TestHttpClientFactory(async (_, token) =>
        {
            lock (counterLock)
            {
                active++;
                peak = Math.Max(peak, active);
                entered++;
                if (entered == 4)
                {
                    firstBatch.TrySetResult();
                }
            }
            try
            {
                await responses.WaitAsync(token);
                return TestHttpClientFactory.JsonResponse("{}");
            }
            finally
            {
                lock (counterLock)
                {
                    active--;
                    completed++;
                    if (completed == 12)
                    {
                        drained.TrySetResult();
                    }
                    else if (completed == 13)
                    {
                        laterRequest.TrySetResult();
                    }
                }
            }
        });
        var sessions = new Mock<ISessionManager>();
        using var service = new ServerEntryPoint(sessions.Object, factory, NullLoggerFactory.Instance, Mock.Of<IUserDataManager>());
        await service.StartAsync(TestContext.Current.CancellationToken);
        var playback = new PlaybackProgressEventArgs { Item = new Audio { Name = "Song", Artists = ["Artist"] }, Users = [user] };
        for (var index = 0; index < 12; index++)
        {
            sessions.Raise(value => value.PlaybackStart += null, playback);
        }
        await firstBatch.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        responses.Release(12);
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        sessions.Raise(value => value.PlaybackStart += null, playback);
        responses.Release();
        await laterRequest.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        factory.Requests.Count.ShouldBe(13);
        peak.ShouldBe(4);
        active.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlaybackEventsSubmitExactlyOnceInTheConfiguredMode(bool alternativeMode)
    {
        var user = ConfigureUser();
        Plugin.Instance!.PluginConfiguration.LastfmUsers[0].Options.AlternativeMode = alternativeMode;
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var factory = new TestHttpClientFactory(async (request, token) =>
        {
            var form = TestHttpClientFactory.ParseForm(await request.Content!.ReadAsStringAsync(token));
            if (form["method"] == "track.scrobble")
            {
                submitted.TrySetResult();
            }
            return TestHttpClientFactory.JsonResponse("""{"scrobbles":{"@attr":{"accepted":"1","ignored":"0"}}}""");
        });
        var sessions = new Mock<ISessionManager>();
        var userData = new Mock<IUserDataManager>();
        var clock = new TestTimeProvider();
        var startedAt = clock.GetUtcNow();
        using var service = new ServerEntryPoint(sessions.Object, factory, NullLoggerFactory.Instance, userData.Object, clock);
        await service.StartAsync(TestContext.Current.CancellationToken);
        var track = new Audio { Id = Guid.NewGuid(), Name = "Song", Artists = ["Artist"], RunTimeTicks = TimeSpan.FromMinutes(4).Ticks };
        var progress = new PlaybackProgressEventArgs { Item = track, Users = [user], PlaySessionId = "session", PlaybackPositionTicks = 0 };
        sessions.Raise(value => value.PlaybackStart += null, progress);
        clock.Advance(120);
        progress.PlaybackPositionTicks = TimeSpan.FromSeconds(120).Ticks;
        sessions.Raise(value => value.PlaybackProgress += null, progress);
        userData.Raise(value => value.UserDataSaved += null, new UserDataSaveEventArgs
        {
            Item = track,
            UserId = user.Id,
            SaveReason = UserDataSaveReason.PlaybackFinished,
            UserData = new UserItemData { Key = track.Id.ToString() }
        });
        sessions.Raise(value => value.PlaybackStopped += null, new PlaybackStopEventArgs
        {
            Item = track,
            Users = [user],
            PlaySessionId = "session",
            PlaybackPositionTicks = TimeSpan.FromSeconds(120).Ticks
        });
        await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);
        var scrobble = factory.Requests.Where(request => request.Form["method"] == "track.scrobble").ShouldHaveSingleItem();
        scrobble.Form["timestamp"].ShouldBe(startedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ImportQueriesOnlyMatchingArtistsAndDoesNotChangeUnrelatedTracks()
    {
        var user = ConfigureUser();
        var artist = new MusicArtist { Id = Guid.NewGuid(), Name = "Matched Artist", ProviderIds = new Dictionary<string, string> { ["MusicBrainzArtist"] = "artist-mbid" } };
        var matched = new Audio { Id = Guid.NewGuid(), Name = "Loved Song" };
        var unrelated = new Audio { Id = Guid.NewGuid(), Name = "Unrelated Song" };
        var library = new Mock<ILibraryManager>(MockBehavior.Strict);
        library.Setup(value => value.GetArtists(It.IsAny<InternalItemsQuery>())).Returns(() => new() { Items = [(artist, new())] });
        library.Setup(value => value.GetItemList(It.Is<InternalItemsQuery>(query => query.ArtistIds.Length == 1 && query.ArtistIds[0] == artist.Id && query.IncludeItemTypes.Contains(Jellyfin.Data.Enums.BaseItemKind.Audio))))
            .Returns(new BaseItem[] { matched, unrelated });
        var data = new UserItemData { Key = matched.Id.ToString() };
        var userData = new Mock<IUserDataManager>(MockBehavior.Strict);
        userData.Setup(value => value.GetUserData(user, matched)).Returns(data);
        userData.Setup(value => value.SaveUserData(user, matched, data, UserDataSaveReason.UpdateUserRating, It.IsAny<CancellationToken>()));
        using var factory = TestHttpClientFactory.Responding(LovedTracksJson);
        using var task = ImportTask(factory, user, library, userData);
        await task.ExecuteAsync(new InlineProgress(), TestContext.Current.CancellationToken);
        data.IsFavorite.ShouldBeTrue();
        userData.VerifyAll();
        library.VerifyAll();
        Plugin.Syncing.ShouldBeFalse();
    }

    [Fact]
    public async Task ImportFailureAlwaysClearsFeedbackSuppression()
    {
        var user = ConfigureUser();
        using var factory = TestHttpClientFactory.Responding("not-json");
        using var task = ImportTask(factory, user, new Mock<ILibraryManager>(), new Mock<IUserDataManager>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => task.ExecuteAsync(new InlineProgress(), TestContext.Current.CancellationToken));
        Plugin.Syncing.ShouldBeFalse();
    }

    [Fact]
    public async Task CancellingActiveImportClearsFeedbackSuppression()
    {
        var user = ConfigureUser();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var factory = new TestHttpClientFactory(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TestHttpClientFactory.JsonResponse("{}");
        });
        using var task = ImportTask(factory, user, new Mock<ILibraryManager>(), new Mock<IUserDataManager>());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var running = task.ExecuteAsync(new InlineProgress(), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Plugin.Syncing.ShouldBeTrue();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        Plugin.Syncing.ShouldBeFalse();
    }

    [Fact]
    public async Task InvalidPaginationFailsWithoutRepeatingRequests()
    {
        var user = ConfigureUser();
        using var factory = TestHttpClientFactory.Responding(LovedTracksJson.Replace("\"page\":\"1\",\"totalPages\":\"1\"", "\"page\":\"0\",\"totalPages\":\"2\"", StringComparison.Ordinal));
        using var task = ImportTask(factory, user, new Mock<ILibraryManager>(), new Mock<IUserDataManager>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => task.ExecuteAsync(new InlineProgress(), TestContext.Current.CancellationToken));
        factory.Requests.ShouldHaveSingleItem();
        Plugin.Syncing.ShouldBeFalse();
    }

    private const string LovedTracksJson = """{"lovedtracks":{"track":[{"name":"Loved Song","artist":{"name":"Matched Artist","mbid":"artist-mbid"}}],"@attr":{"page":"1","totalPages":"1","total":"1"}}}""";

    private static User ConfigureUser()
    {
        var paths = new Mock<IApplicationPaths>();
        var directory = Path.Combine(Path.GetTempPath(), "lastfm-tests", Guid.NewGuid().ToString("N"));
        paths.SetupGet(value => value.PluginConfigurationsPath).Returns(directory);
        paths.SetupGet(value => value.PluginsPath).Returns(directory);
        paths.SetupGet(value => value.DataPath).Returns(directory);
        var serializer = new Mock<IXmlSerializer>();
        serializer.Setup(value => value.DeserializeFromFile(typeof(PluginConfiguration), It.IsAny<string>())).Returns(new PluginConfiguration());
        var plugin = new Plugin(paths.Object, serializer.Object);
        var user = new User("listener", "authentication-provider", "password-reset-provider") { Id = Guid.NewGuid() };
        plugin.PluginConfiguration.LastfmUsers = [new LastfmUser { Username = "listener", SessionKey = "fake-session", MediaBrowserUserId = user.Id, Options = new LastFmUserOptions { Scrobble = true, SyncFavourites = true } }];
        return user;
    }

    private static ImportLastfmData ImportTask(TestHttpClientFactory factory, User user, Mock<ILibraryManager> library, Mock<IUserDataManager> userData)
    {
        var users = new Mock<IUserManager>();
        users.Setup(value => value.GetUsers()).Returns([user]);
        return new ImportLastfmData(factory, users.Object, userData.Object, library.Object, NullLoggerFactory.Instance);
    }

    private sealed class InlineProgress : IProgress<double>
    {
        public void Report(double value) => value.ShouldBeInRange(0, 100);
    }
}
