using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Tasks;
using JellySin.Plugin.Lastfm.Tests.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Features;

public sealed class LibraryScanRematchTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AScanMatchesOnlyThePendingTracksAndNotTheKeptMatches()
    {
        using var f = await SyncedAsync(3, new MusicTrack("Artist", "Arrives later"), new MusicTrack("Artist", "Still missing"));
        var matches = f.Library.MatchCalls;
        var later = f.AddLocal("Arrives later");
        f.AddLocal("Not loved");
        await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.Equal(matches + 2, f.Library.MatchCalls);
        Assert.True(f.Library.Items[later.Id].Favourite);
    }

    [Fact]
    public async Task ATrackAddedUnderAKeptTitleMatchesThatTrackAgain()
    {
        using var f = await SyncedAsync(2);
        var matches = f.Library.MatchCalls;
        var duplicate = f.AddLocal("Song 0");
        await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.Equal(matches + 1, f.Library.MatchCalls);
        Assert.False(f.Library.Items[duplicate.Id].Favourite);
    }

    [Fact]
    public async Task ATrackAddedWithAKeptMusicBrainzIdMatchesThatTrackAgain()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var mbid = Guid.NewGuid().ToString();
        var kept = f.AddLocal("Tagged", true, mbid: mbid);
        f.Loved.Add(kept.Track);
        await f.EnableAndSyncAsync(Ct);
        var matches = f.Library.MatchCalls;
        f.AddLocal("Other title", mbid: mbid);
        await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.Equal(matches + 1, f.Library.MatchCalls);
    }

    [Fact]
    public async Task ARemovedTrackIsMatchedAgain()
    {
        using var f = await SyncedAsync(2);
        var matches = f.Library.MatchCalls;
        f.Library.Items.Remove(f.Library.Items.Values.Single(item => item.Track.Title == "Song 0").Id);
        await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.Equal(matches + 1, f.Library.MatchCalls);
    }

    [Fact]
    public async Task ChangesNoLongerAllKnownMatchEveryTrackAgain()
    {
        using var f = await SyncedAsync(3);
        var matches = f.Library.MatchCalls;
        f.Library.ChangesForgotten = true;
        f.AddLocal("Not loved");
        await f.Favourites.SyncAsync(f.Core.UserId, Ct);
        Assert.Equal(matches + 3, f.Library.MatchCalls);
    }

    [Fact]
    public void OneChangedTrackInTenThousandKeptMatchesIsTheOnlyOneMatchedAgain()
    {
        var generation = new Mock<ILibraryGeneration>();
        long current = 1;
        var changes = new LibraryChanges();
        generation.SetupGet(value => value.Current).Returns(() => current);
        generation.Setup(value => value.Access(It.IsAny<Guid>())).Returns("all");
        generation.Setup(value => value.ChangesSince(1)).Returns(changes);
        var reuse = new LovedReuse(generation.Object, TestTime.Clock());
        var user = Guid.NewGuid();
        var tracks = Enumerable.Range(0, LovedReuse.MaxTracks).Select(index => new MusicTrack("Artist", "Song " + index)).ToArray();
        var calls = 0;
        Guid? Match(MusicTrack track) { calls++; return Guid.NewGuid(); }
        reuse.Match(user, tracks, Match);
        changes.Note(Guid.NewGuid(), "Song 17", null, true);
        current = 2;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        reuse.Match(user, tracks, Match);
        TestContext.Current.TestOutputHelper?.WriteLine($"Kept-match refresh of {tracks.Length} tracks: {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms.");
        Assert.Equal(LovedReuse.MaxTracks + 1, calls);
    }

    [Fact]
    public void TrackEventsAreKeptAsChangesAndOtherEventsAreNot()
    {
        var library = new Mock<ILibraryManager>();
        using var generation = new LibraryGeneration(library.Object, Mock.Of<IUserManager>());
        var added = new Audio { Id = Guid.NewGuid(), Name = " Added ", ProviderIds = new() { ["MusicBrainzTrack"] = Guid.NewGuid().ToString() } };
        var removed = new Audio { Id = Guid.NewGuid(), Name = "Removed" };
        library.Raise(value => value.ItemAdded += null, library.Object, new ItemChangeEventArgs { Item = added });
        library.Raise(value => value.ItemRemoved += null, library.Object, new ItemChangeEventArgs { Item = removed });
        library.Raise(value => value.ItemUpdated += null, library.Object, new ItemChangeEventArgs { Item = new MusicAlbum() });
        var changes = Assert.IsType<LibraryChanges>(generation.ChangesSince(0));
        Assert.Equal(["ADDED"], changes.Titles);
        Assert.Equal([Guid.Parse(added.ProviderIds["MusicBrainzTrack"])], changes.Mbids);
        Assert.Equal([added.Id, removed.Id], changes.Items);
        Assert.Equal(3, generation.Current);
        Assert.Empty(Assert.IsType<LibraryChanges>(generation.ChangesSince(3)).Items);
    }

    [Fact]
    public void ARemovedFolderOrMoreThanTheKeptChangesAreNoLongerKnown()
    {
        var library = new Mock<ILibraryManager>();
        using var generation = new LibraryGeneration(library.Object, Mock.Of<IUserManager>());
        library.Raise(value => value.ItemRemoved += null, library.Object, new ItemChangeEventArgs { Item = new Folder() });
        Assert.Null(generation.ChangesSince(0));
        Assert.NotNull(generation.ChangesSince(1));
        // The folder plus one change more than are kept: the first track change is forgotten as well.
        for (var index = 0; index <= LibraryGeneration.MaxChanges; index++)
            library.Raise(value => value.ItemAdded += null, library.Object, new ItemChangeEventArgs { Item = new Audio { Id = Guid.NewGuid() } });
        Assert.Null(generation.ChangesSince(1));
        Assert.NotNull(generation.ChangesSince(2));
    }

    [Fact]
    public async Task ThePostScanTaskStartsTheRunAndDoesNotWaitForIt()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var later = f.AddLocal("Arrives later");
        f.Loved.Add(later.Track);
        await f.Favourites.SetEnabledAsync(f.Core.UserId, true, Ct);
        var matching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        f.Library.OnMatch = _ => { matching.TrySetResult(); release.Wait(Ct); };
        var task = new LibraryScanRematchTask(f.Core.Accounts, f.Runs, NullLogger<LibraryScanRematchTask>.Instance);
        await task.Run(Mock.Of<IProgress<double>>(), Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        await matching.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(f.Runs.IsRunning(f.Core.UserId));
        release.Set();
        await f.ReconciliationEndedAsync(Ct);
        Assert.True(f.Library.Items[later.Id].Favourite);
    }

    [Fact]
    public async Task ThePostScanTaskStartsNoRunOnceTheServerStops()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        f.Runs.Dispose();
        Assert.False(f.Runs.TryStart(f.Core.UserId));
        var task = new LibraryScanRematchTask(f.Core.Accounts, f.Runs, NullLogger<LibraryScanRematchTask>.Instance);
        await task.Run(Mock.Of<IProgress<double>>(), Ct);
        Assert.False(f.Runs.IsRunning(f.Core.UserId));
    }

    private static async Task<FeatureFixture> SyncedAsync(int count, params MusicTrack[] lovedElsewhere)
    {
        var f = new FeatureFixture();
        await f.InitializeAsync();
        f.Loved.AddRange(lovedElsewhere);
        for (var index = 0; index < count; index++) f.Loved.Add(f.AddLocal("Song " + index, true).Track);
        await f.EnableAndSyncAsync(Ct);
        f.Core.Client.Calls.Clear();
        return f;
    }
}
