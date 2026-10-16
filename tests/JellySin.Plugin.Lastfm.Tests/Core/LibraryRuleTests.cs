using Jellyfin.Database.Implementations.Entities;
using JellySin.Plugin.Lastfm.Api;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Playback;
using JellySin.Plugin.Lastfm.Storage;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class LibraryRuleTests
{
    private readonly Guid _lib1 = Guid.NewGuid();
    private readonly Guid _lib2 = Guid.NewGuid();

    [Fact]
    public async Task AllowedLibraryScrobbles()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var config = new PluginConfiguration { Enabled = true, LibraryMode = LibraryFilterMode.AllowList, LibraryIds = [_lib1] };
        var libraryManager = CreateLibraryManager((_lib1, "/media/music"));
        var libraryRules = new LibraryRuleService(fixture.Store, libraryManager.Object, () => config);
        await libraryRules.InitializeAsync(TestContext.Current.CancellationToken);

        var sessions = new Mock<ISessionManager>();
        var plugins = new Mock<IPluginManager>();
        plugins.SetupGet(v => v.Plugins).Returns([]);
        var tracker = new PlaybackTracker(fixture.Clock);
        var outbox = new ScrobbleOutbox(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock);
        using var playback = new PlaybackService(sessions.Object, plugins.Object, tracker, outbox, fixture.Accounts, libraryRules, fixture.Clock, NullLogger<PlaybackService>.Instance);
        await playback.StartAsync(TestContext.Current.CancellationToken);

        var args = CreateAudioArgs(fixture.UserId);
        sessions.Raise(v => v.PlaybackStart += null, args);
        AdvanceAndReportProgress(sessions, args, fixture.Clock, 60);
        await playback.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, (await outbox.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Pending);
    }

    [Fact]
    public async Task AdminBlockedLibraryNeverCapturedEvenIfUserTriesToInclude()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var config = new PluginConfiguration { Enabled = true, LibraryMode = LibraryFilterMode.BlockList, LibraryIds = [_lib1] };
        var libraryManager = CreateLibraryManager((_lib1, "/media/music"));
        var libraryRules = new LibraryRuleService(fixture.Store, libraryManager.Object, () => config);
        await libraryRules.InitializeAsync(TestContext.Current.CancellationToken);

        var sessions = new Mock<ISessionManager>();
        var plugins = new Mock<IPluginManager>();
        plugins.SetupGet(v => v.Plugins).Returns([]);
        var tracker = new PlaybackTracker(fixture.Clock);
        var outbox = new ScrobbleOutbox(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock);
        using var playback = new PlaybackService(sessions.Object, plugins.Object, tracker, outbox, fixture.Accounts, libraryRules, fixture.Clock, NullLogger<PlaybackService>.Instance);
        await playback.StartAsync(TestContext.Current.CancellationToken);

        var args = CreateAudioArgs(fixture.UserId);
        sessions.Raise(v => v.PlaybackStart += null, args);
        AdvanceAndReportProgress(sessions, args, fixture.Clock, 60);
        await playback.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, (await outbox.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Pending);
    }

    [Fact]
    public async Task UserExcludedLibrarySkippedForThatUserOnlyOtherUserInSameSessionStillCaptured()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var user2 = Guid.NewGuid();
        await fixture.ConnectAsync(user2);

        var config = new PluginConfiguration { Enabled = true, LibraryMode = LibraryFilterMode.All };
        var libraryManager = CreateLibraryManager((_lib1, "/media/music"));
        var libraryRules = new LibraryRuleService(fixture.Store, libraryManager.Object, () => config);
        await libraryRules.InitializeAsync(TestContext.Current.CancellationToken);
        await libraryRules.SetUserExcludedLibrariesAsync(fixture.UserId, [_lib1], TestContext.Current.CancellationToken);

        var sessions = new Mock<ISessionManager>();
        var plugins = new Mock<IPluginManager>();
        plugins.SetupGet(v => v.Plugins).Returns([]);
        var tracker = new PlaybackTracker(fixture.Clock);
        var outbox = new ScrobbleOutbox(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock);
        using var playback = new PlaybackService(sessions.Object, plugins.Object, tracker, outbox, fixture.Accounts, libraryRules, fixture.Clock, NullLogger<PlaybackService>.Instance);
        await playback.StartAsync(TestContext.Current.CancellationToken);

        var args = CreateAudioArgs(fixture.UserId, user2);
        sessions.Raise(v => v.PlaybackStart += null, args);
        AdvanceAndReportProgress(sessions, args, fixture.Clock, 60);
        await playback.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, (await outbox.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Pending);
        Assert.Equal(1, (await outbox.GetStatusAsync(user2, TestContext.Current.CancellationToken)).Pending);
    }

    [Fact]
    public async Task BoundaryEmptyAllowListBlocksAll()
    {
        using var fixture = new CoreFixture();
        var config = new PluginConfiguration { Enabled = true, LibraryMode = LibraryFilterMode.AllowList, LibraryIds = [] };
        var libraryManager = CreateLibraryManager((_lib1, "/media/music"));
        var libraryRules = new LibraryRuleService(fixture.Store, libraryManager.Object, () => config);
        await libraryRules.InitializeAsync(TestContext.Current.CancellationToken);

        var audio = new Audio { Id = Guid.NewGuid(), Name = "Track", Artists = ["Artist"], Path = "/media/music/song.mp3", RunTimeTicks = TimeSpan.FromSeconds(120).Ticks };
        Assert.False(libraryRules.IsScrobblingAllowed(fixture.UserId, audio));
    }

    [Fact]
    public void BoundaryItemWithNoLibraryResolvableFollowsModeSpecification()
    {
        using var fixture = new CoreFixture();
        var audio = new Audio { Id = Guid.NewGuid(), Name = "Track", Artists = ["Artist"], Path = "/unmapped/song.mp3", RunTimeTicks = TimeSpan.FromSeconds(120).Ticks };

        var allConfig = new PluginConfiguration { Enabled = true, LibraryMode = LibraryFilterMode.All };
        var allRules = new LibraryRuleService(fixture.Store, configProvider: () => allConfig);
        Assert.True(allRules.IsScrobblingAllowed(fixture.UserId, audio));

        var blockConfig = new PluginConfiguration { Enabled = true, LibraryMode = LibraryFilterMode.BlockList, LibraryIds = [_lib1] };
        var blockRules = new LibraryRuleService(fixture.Store, configProvider: () => blockConfig);
        Assert.True(blockRules.IsScrobblingAllowed(fixture.UserId, audio));

        var allowConfig = new PluginConfiguration { Enabled = true, LibraryMode = LibraryFilterMode.AllowList, LibraryIds = [_lib1] };
        var allowRules = new LibraryRuleService(fixture.Store, configProvider: () => allowConfig);
        Assert.False(allowRules.IsScrobblingAllowed(fixture.UserId, audio));
    }

    [Fact]
    public async Task BoundaryMaxSizeLibraryListHandledGracefully()
    {
        using var fixture = new CoreFixture();
        var excessIds = Enumerable.Range(0, 300).Select(_ => Guid.NewGuid()).ToArray();
        var config = new PluginConfiguration { Enabled = true, LibraryMode = LibraryFilterMode.AllowList, LibraryIds = excessIds };

        Assert.Equal(PluginConfiguration.MaxConfiguredLibraries, config.LibraryIds.Length);

        var libraryManager = CreateLibraryManager((config.LibraryIds[0], "/media/music"));
        var libraryRules = new LibraryRuleService(fixture.Store, libraryManager.Object, () => config);
        await libraryRules.InitializeAsync(TestContext.Current.CancellationToken);

        var audio = new Audio { Id = Guid.NewGuid(), Name = "Track", Artists = ["Artist"], Path = "/media/music/song.mp3", RunTimeTicks = TimeSpan.FromSeconds(120).Ticks };
        Assert.True(libraryRules.IsScrobblingAllowed(fixture.UserId, audio));
    }

    [Fact]
    public async Task BoundaryDeletedOrUnknownLibraryIdIgnoredGracefully()
    {
        using var fixture = new CoreFixture();
        var deletedId = Guid.NewGuid();
        var config = new PluginConfiguration { Enabled = true, LibraryMode = LibraryFilterMode.AllowList, LibraryIds = [_lib1, deletedId] };
        var libraryManager = CreateLibraryManager((_lib1, "/media/music"));
        var libraryRules = new LibraryRuleService(fixture.Store, libraryManager.Object, () => config);
        await libraryRules.InitializeAsync(TestContext.Current.CancellationToken);

        var audio = new Audio { Id = Guid.NewGuid(), Name = "Track", Artists = ["Artist"], Path = "/media/music/song.mp3", RunTimeTicks = TimeSpan.FromSeconds(120).Ticks };
        Assert.True(libraryRules.IsScrobblingAllowed(fixture.UserId, audio));
    }

    [Fact]
    public async Task RestartRecoveryPerUserRulePersistsThroughIStateStoreReload()
    {
        using var fixture = new CoreFixture();
        var libraryManager = CreateLibraryManager((_lib1, "/media/music"), (_lib2, "/media/audiobooks"));
        var libraryRules = new LibraryRuleService(fixture.Store, libraryManager.Object);
        await libraryRules.SetUserExcludedLibrariesAsync(fixture.UserId, [_lib1, _lib2], TestContext.Current.CancellationToken);

        var restored = new LibraryRuleService(fixture.Store, libraryManager.Object);
        await restored.InitializeAsync(TestContext.Current.CancellationToken);

        var excluded = await restored.GetUserExcludedLibrariesAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.Equal(2, excluded.Count);
        Assert.Contains(_lib1, excluded);
        Assert.Contains(_lib2, excluded);

        var audio = new Audio { Id = Guid.NewGuid(), Name = "Track", Artists = ["Artist"], Path = "/media/music/song.mp3", RunTimeTicks = TimeSpan.FromSeconds(120).Ticks };
        Assert.False(restored.IsScrobblingAllowed(fixture.UserId, audio));
    }

    [Fact]
    public async Task PathPrefixMatchesAudioItemToVirtualFolder()
    {
        using var fixture = new CoreFixture();
        var libraryManager = CreateLibraryManager((_lib1, "/media/music"));
        var rules = new LibraryRuleService(fixture.Store, libraryManager.Object);
        await rules.InitializeAsync(TestContext.Current.CancellationToken);

        var audio = new Audio { Id = Guid.NewGuid(), Name = "Track", Artists = ["Artist"], Path = "/media/music/Artist/Album/song.mp3", RunTimeTicks = TimeSpan.FromSeconds(120).Ticks };
        var resolved = rules.ResolveItemLibraries(audio);
        Assert.NotNull(resolved);
        Assert.Equal([_lib1], resolved);
    }

    [Fact]
    public async Task NestedLibraryLocationsResolveToInnerLibraryInBothInsertionOrders()
    {
        using var fixture = new CoreFixture();
        var musicId = Guid.NewGuid();
        var audiobooksId = Guid.NewGuid();

        // Order 1: /music added before /music/audiobooks
        var lmOrder1 = new Mock<ILibraryManager>();
        lmOrder1.Setup(m => m.GetVirtualFolders()).Returns(
        [
            new VirtualFolderInfo { ItemId = musicId.ToString(), Name = "Music", Locations = ["/music"] },
            new VirtualFolderInfo { ItemId = audiobooksId.ToString(), Name = "Audiobooks", Locations = ["/music/audiobooks"] }
        ]);
        var rules1 = new LibraryRuleService(fixture.Store, lmOrder1.Object);
        await rules1.InitializeAsync(TestContext.Current.CancellationToken);

        var innerItem = new Audio { Id = Guid.NewGuid(), Path = "/music/audiobooks/author/book/ch1.mp3" };
        var outerItem = new Audio { Id = Guid.NewGuid(), Path = "/music/rock/song.mp3" };
        Assert.Equal([audiobooksId], rules1.ResolveItemLibraries(innerItem));
        Assert.Equal([musicId], rules1.ResolveItemLibraries(outerItem));

        // Order 2: /music/audiobooks added before /music
        var lmOrder2 = new Mock<ILibraryManager>();
        lmOrder2.Setup(m => m.GetVirtualFolders()).Returns(
        [
            new VirtualFolderInfo { ItemId = audiobooksId.ToString(), Name = "Audiobooks", Locations = ["/music/audiobooks"] },
            new VirtualFolderInfo { ItemId = musicId.ToString(), Name = "Music", Locations = ["/music"] }
        ]);
        var rules2 = new LibraryRuleService(fixture.Store, lmOrder2.Object);
        await rules2.InitializeAsync(TestContext.Current.CancellationToken);

        Assert.Equal([audiobooksId], rules2.ResolveItemLibraries(innerItem));
        Assert.Equal([musicId], rules2.ResolveItemLibraries(outerItem));
    }

    [Fact]
    public async Task LibraryAddedOrRemovedAfterStartupRefreshesPrefixesViaChangeEvents()
    {
        using var fixture = new CoreFixture();
        var folders = new List<VirtualFolderInfo>
        {
            new() { ItemId = _lib1.ToString(), Name = "Music", Locations = ["/media/music"] }
        };
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(m => m.GetVirtualFolders()).Returns(() => folders);

        var rules = new LibraryRuleService(fixture.Store, libraryManager.Object);
        await rules.InitializeAsync(TestContext.Current.CancellationToken);

        var musicTrack = new Audio { Id = Guid.NewGuid(), Path = "/media/music/song.mp3" };
        var podcastTrack = new Audio { Id = Guid.NewGuid(), Path = "/media/podcasts/ep1.mp3" };

        Assert.Equal([_lib1], rules.ResolveItemLibraries(musicTrack));
        Assert.Null(rules.ResolveItemLibraries(podcastTrack));

        // Add podcasts library after startup and fire ItemAdded
        folders.Add(new VirtualFolderInfo { ItemId = _lib2.ToString(), Name = "Podcasts", Locations = ["/media/podcasts"] });
        libraryManager.Raise(m => m.ItemAdded += null, libraryManager.Object, new ItemChangeEventArgs { Item = new CollectionFolder() });
        await rules.PendingRebuild;

        Assert.Equal([_lib2], rules.ResolveItemLibraries(podcastTrack));

        // Remove music library and fire ItemRemoved
        folders.RemoveAt(0);
        libraryManager.Raise(m => m.ItemRemoved += null, libraryManager.Object, new ItemChangeEventArgs { Item = new CollectionFolder() });
        await rules.PendingRebuild;

        Assert.Null(rules.ResolveItemLibraries(musicTrack));
        Assert.Equal([_lib2], rules.ResolveItemLibraries(podcastTrack));
    }

    [Fact]
    public async Task ScanEventBurstCoalescesIntoOneRunningAndOneQueuedRebuild()
    {
        using var fixture = new CoreFixture();
        using var release = new ManualResetEventSlim(false);
        var folders = new List<VirtualFolderInfo> { new() { ItemId = _lib1.ToString(), Name = "Music", Locations = ["/media/music"] } };
        var calls = 0;
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(m => m.GetVirtualFolders()).Returns(() =>
        {
            if (Interlocked.Increment(ref calls) == 2) release.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            return folders.ToList();
        });
        using var rules = new LibraryRuleService(fixture.Store, libraryManager.Object);
        await rules.InitializeAsync(TestContext.Current.CancellationToken);
        libraryManager.Raise(m => m.ItemUpdated += null, libraryManager.Object, new ItemChangeEventArgs { Item = new CollectionFolder() });
        for (var i = 0; i < 200 && Volatile.Read(ref calls) < 2; i++) await Task.Delay(10, TestContext.Current.CancellationToken);
        folders.Add(new VirtualFolderInfo { ItemId = _lib2.ToString(), Name = "Podcasts", Locations = ["/media/podcasts"] });
        for (var i = 0; i < 100; i++)
            libraryManager.Raise(m => m.ItemUpdated += null, libraryManager.Object, new ItemChangeEventArgs { Item = new CollectionFolder() });
        release.Set();
        await rules.PendingRebuild.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(3, Volatile.Read(ref calls)); // startup, the running rebuild, one queued for the whole burst
        Assert.Equal([_lib2], rules.ResolveItemLibraries(new Audio { Id = Guid.NewGuid(), Path = "/media/podcasts/ep1.mp3" }));
    }

    [Fact]
    public async Task AdminSavesLibraryRulesMidPlayScrobbleStillLands()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();

        // A configuration provider stands in for Plugin.Instance: constructing a Plugin sets that static for every
        // test running in parallel, which would apply this allow-list to their playback.
        var config = new PluginConfiguration
        {
            Enabled = true,
            LibraryMode = LibraryFilterMode.AllowList,
            LibraryIds = [_lib1]
        };

        var libraryManager = CreateLibraryManager((_lib1, "/media/music"), (_lib2, "/media/podcasts"));
        var libraryRules = new LibraryRuleService(fixture.Store, libraryManager.Object, () => config);
        await libraryRules.InitializeAsync(TestContext.Current.CancellationToken);

        var sessions = new Mock<ISessionManager>();
        var plugins = new Mock<IPluginManager>();
        plugins.SetupGet(v => v.Plugins).Returns([]);
        var tracker = new PlaybackTracker(fixture.Clock);
        var outbox = new ScrobbleOutbox(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock);
        using var playback = new PlaybackService(sessions.Object, plugins.Object, tracker, outbox, fixture.Accounts, libraryRules, fixture.Clock, NullLogger<PlaybackService>.Instance);
        await playback.StartAsync(TestContext.Current.CancellationToken);

        var args = CreateAudioArgs(fixture.UserId);
        args.Item.Path = "/media/music/Artist/Album/song.mp3";
        args.Item.RunTimeTicks = TimeSpan.FromSeconds(120).Ticks;

        sessions.Raise(v => v.PlaybackStart += null, args);

        // Advance 30s: mid-play, below scrobble threshold (60s)
        fixture.Clock.Advance(30);
        args.PlaybackPositionTicks += TimeSpan.FromSeconds(30).Ticks;
        sessions.Raise(v => v.PlaybackProgress += null, args);

        // Admin saves new library rules (e.g. changing allowed libraries) mid-play
        config = new PluginConfiguration
        {
            Enabled = true,
            LibraryMode = LibraryFilterMode.AllowList,
            LibraryIds = [_lib1, _lib2]
        };

        // Advance another 30s: reaches 60s (50% runtime scrobble threshold)
        fixture.Clock.Advance(30);
        args.PlaybackPositionTicks += TimeSpan.FromSeconds(30).Ticks;
        sessions.Raise(v => v.PlaybackProgress += null, args);

        await playback.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, (await outbox.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Pending);
        Assert.Equal(0, playback.GetStatus().DroppedSnapshots);
    }

    [Fact]
    public async Task PathNormalizationHandlesBothSeparatorsAndTrailingSeparators()
    {
        using var fixture = new CoreFixture();
        var libraryManager = CreateLibraryManager((_lib1, @"C:\media\music\"));
        var rules = new LibraryRuleService(fixture.Store, libraryManager.Object);
        await rules.InitializeAsync(TestContext.Current.CancellationToken);

        // Item with forward slashes matches Windows location with backslashes
        var forwardSlashAudio = new Audio { Id = Guid.NewGuid(), Path = "C:/media/music/song.mp3" };
        Assert.Equal([_lib1], rules.ResolveItemLibraries(forwardSlashAudio));

        // Sibling folder should NOT match due to trailing separator
        var siblingAudio = new Audio { Id = Guid.NewGuid(), Path = "C:/media/music_backup/song.mp3" };
        Assert.Null(rules.ResolveItemLibraries(siblingAudio));
    }

    [Fact]
    public async Task UserLibrariesControllerResolvesCallerIdentityAndUpdatesExclusions()
    {
        using var fixture = new CoreFixture();
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(m => m.GetVirtualFolders()).Returns(
        [
            new VirtualFolderInfo { ItemId = _lib1.ToString(), Name = "Music", Locations = ["/media/music"] },
            new VirtualFolderInfo { ItemId = _lib2.ToString(), Name = "Audiobooks", Locations = ["/media/audiobooks"] }
        ]);
        var config = new PluginConfiguration { Enabled = true, LibraryMode = LibraryFilterMode.BlockList, LibraryIds = [_lib2] };
        var rules = new LibraryRuleService(fixture.Store, libraryManager.Object, () => config);

        var controller = new UserLibrariesController(rules);
        var httpContext = new DefaultHttpContext();
        httpContext.Items[typeof(CallerFilter)] = Api.CallerSecurityTests.Identity(fixture.UserId);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var view = await controller.Get(TestContext.Current.CancellationToken);
        Assert.Equal("BlockList", view.Mode);
        Assert.Equal(2, view.Libraries.Count);
        var lib1View = Assert.Single(view.Libraries, l => l.Id == _lib1);
        Assert.True(lib1View.IsAdminAllowed);
        Assert.True(lib1View.IsScrobbled);
        Assert.False(lib1View.IsUserExcluded);

        var lib2View = Assert.Single(view.Libraries, l => l.Id == _lib2);
        Assert.False(lib2View.IsAdminAllowed);
        Assert.False(lib2View.IsScrobbled);

        var updated = await controller.Update(new UpdateUserLibrariesRequest([_lib1]), TestContext.Current.CancellationToken);
        var updatedLib1 = Assert.Single(updated.Libraries, l => l.Id == _lib1);
        Assert.True(updatedLib1.IsAdminAllowed);
        Assert.True(updatedLib1.IsUserExcluded);
        Assert.False(updatedLib1.IsScrobbled);
    }

    private static Mock<ILibraryManager> CreateLibraryManager(params (Guid Id, string Path)[] folders)
    {
        var mock = new Mock<ILibraryManager>();
        mock.Setup(m => m.GetVirtualFolders()).Returns(
            folders.Select(f => new VirtualFolderInfo { ItemId = f.Id.ToString(), Name = "Lib", Locations = [f.Path] }).ToList());
        return mock;
    }

    private static PlaybackProgressEventArgs CreateAudioArgs(params Guid[] userIds) => new()
    {
        Item = new Audio
        {
            Id = Guid.NewGuid(),
            Name = "Track",
            Artists = ["Artist"],
            Path = "/media/music/Artist/Album/song.mp3",
            RunTimeTicks = TimeSpan.FromSeconds(120).Ticks
        },
        Users = userIds.Select(id => new User("listener", "auth", "password") { Id = id }).ToList(),
        PlaySessionId = "test-session",
        PlaybackPositionTicks = 0
    };

    private static void AdvanceAndReportProgress(Mock<ISessionManager> sessions, PlaybackProgressEventArgs args, TestClock clock, int seconds)
    {
        for (var elapsed = 0; elapsed < seconds; elapsed += 30)
        {
            var delta = Math.Min(30, seconds - elapsed);
            clock.Advance(delta);
            args.PlaybackPositionTicks += TimeSpan.FromSeconds(delta).Ticks;
            sessions.Raise(v => v.PlaybackProgress += null, args);
        }
    }
}
