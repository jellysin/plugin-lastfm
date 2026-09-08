using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Lastfm.Utils;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Jellyfin.Plugin.Lastfm.Tests;

public sealed class PlaybackTrackerTests
{
    private readonly TestTimeProvider _clock = new();
    private readonly User _user = new("listener", "auth", "reset") { Id = Guid.NewGuid() };
    private readonly Audio _track = new() { Id = Guid.NewGuid(), Name = "Song", Artists = ["Artist"], RunTimeTicks = TimeSpan.FromMinutes(4).Ticks };

    [Fact]
    public void NormalPlaybackUsesListenedTimeAndOriginalStartTime()
    {
        var tracker = new PlaybackTracker(_clock);
        var startedAt = _clock.GetUtcNow();
        tracker.Start(Progress(0));
        _clock.Advance(119);
        tracker.Progress(Progress(119));
        _clock.Advance(1);
        var scrobble = tracker.Stop(Stop(120)).ShouldHaveSingleItem();
        scrobble.PlayedTicks.ShouldBe(TimeSpan.FromSeconds(120).Ticks);
        scrobble.StartedAt.ShouldBe(startedAt);
        scrobble.UserId.ShouldBe(_user.Id);
        scrobble.Item.ShouldBeSameAs(_track);
        scrobble.AlternativeMode.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AutomaticProgressCannotEstablishOrAccrueListening(bool observedStart)
    {
        var tracker = new PlaybackTracker(_clock);
        if (observedStart)
        {
            tracker.Start(Progress(0));
        }
        var automatic = Progress(0);
        automatic.IsAutomated = true;
        tracker.Progress(automatic);
        _clock.Advance(120);
        automatic.PlaybackPositionTicks = TimeSpan.FromSeconds(120).Ticks;
        tracker.Progress(automatic);
        tracker.UserDataSaved(_user.Id, _track.Id).ShouldBeEmpty();
        tracker.Stop(Stop(observedStart ? 0 : 120)).ShouldBeEmpty();
    }

    [Fact]
    public void AutomaticProgressDoesNotReplaceTheLastClientReportedPosition()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Start(Progress(0));
        _clock.Advance(60);
        var automatic = Progress(180);
        automatic.IsAutomated = true;
        tracker.Progress(automatic);
        _clock.Advance(60);
        tracker.Progress(Progress(120));
        tracker.Stop(Stop(120)).ShouldHaveSingleItem().PlayedTicks.ShouldBe(TimeSpan.FromSeconds(120).Ticks);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(120)]
    public async Task IdleCleanupPreservesObservedListeningWithoutCreditingItsExtrapolatedPosition(int observedSeconds)
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Start(Progress(0));
        _clock.Advance(observedSeconds);
        tracker.Progress(Progress(observedSeconds));
        await using var session = new SessionInfo(Mock.Of<ISessionManager>(), NullLogger.Instance)
        {
            LastPlaybackCheckIn = _clock.GetUtcNow().UtcDateTime
        };
        _clock.Advance(301);
        var stop = Stop(observedSeconds + 301);
        stop.Session = session;
        var scrobbles = tracker.Stop(stop);
        if (observedSeconds == 0)
        {
            scrobbles.ShouldBeEmpty();
        }
        else
        {
            scrobbles.ShouldHaveSingleItem().PlayedTicks.ShouldBe(TimeSpan.FromSeconds(observedSeconds).Ticks);
        }
    }

    [Theory]
    [InlineData(0, 179, 180)]
    [InlineData(118, 119, 120)]
    public void SeekOrResumePositionDoesNotCountAsListenedTime(int initialPosition, int progressPosition, int stopPosition)
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Start(Progress(initialPosition));
        _clock.Advance(1);
        tracker.Progress(Progress(progressPosition));
        _clock.Advance(1);
        tracker.Stop(Stop(stopPosition)).ShouldBeEmpty();
    }

    [Fact]
    public void PausedWallClockTimeIsExcluded()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Start(Progress(0));
        _clock.Advance(60);
        tracker.Progress(Progress(60, paused: true));
        _clock.Advance(600);
        tracker.Progress(Progress(60));
        _clock.Advance(60);
        tracker.Stop(Stop(120)).ShouldHaveSingleItem().PlayedTicks.ShouldBe(TimeSpan.FromSeconds(120).Ticks);
    }

    [Fact]
    public void BackwardSeekDoesNotSubtractPreviouslyListenedTime()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Start(Progress(0));
        _clock.Advance(60);
        tracker.Progress(Progress(60));
        _clock.Advance(1);
        tracker.Progress(Progress(0));
        _clock.Advance(60);
        tracker.Stop(Stop(60)).ShouldHaveSingleItem().PlayedTicks.ShouldBe(TimeSpan.FromSeconds(120).Ticks);
    }

    [Fact]
    public void StopWithoutAnObservedStartCannotInventListeningHistory()
    {
        new PlaybackTracker(_clock).Stop(Stop(240)).ShouldBeEmpty();
    }

    [Fact]
    public void DifferentSessionsOfTheSameTrackKeepSeparateListeningRecords()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Start(Progress(0, session: "first"));
        tracker.Start(Progress(0, session: "second"));
        _clock.Advance(120);
        tracker.Stop(Stop(120, "first")).ShouldHaveSingleItem();
        tracker.Stop(Stop(120, "second")).ShouldHaveSingleItem();
    }

    [Fact]
    public void EachOriginalParticipantGetsOneScrobbleAndAnUnrelatedUserGetsNone()
    {
        var tracker = new PlaybackTracker(_clock);
        var second = new User("second", "auth", "reset") { Id = Guid.NewGuid() };
        var start = Progress(0);
        start.Users.Add(second);
        tracker.Start(start);
        _clock.Advance(120);
        var stop = Stop(120);
        stop.Users.Add(second);
        var scrobbles = tracker.Stop(stop);
        scrobbles.Select(value => value.UserId).ShouldBe([_user.Id, second.Id], ignoreOrder: true);
        tracker.Stop(stop).ShouldBeEmpty();
        tracker.UserDataSaved(Guid.NewGuid(), _track.Id).ShouldBeEmpty();
    }

    [Fact]
    public void AUserAddedAtStopDoesNotInheritOtherUsersListeningHistory()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Start(Progress(0));
        _clock.Advance(120);
        var stop = Stop(120);
        var lateUser = new User("late", "auth", "reset") { Id = Guid.NewGuid() };
        stop.Users.Add(lateUser);
        tracker.Stop(stop).ShouldHaveSingleItem().UserId.ShouldBe(_user.Id);
        tracker.UserDataSaved(lateUser.Id, _track.Id).ShouldBeEmpty();
    }

    [Fact]
    public void LateParticipantAccumulatesTheirOwnTimeAndStartTimestamp()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Start(Progress(0));
        var lateUser = new User("late", "auth", "reset") { Id = Guid.NewGuid() };
        _clock.Advance(60);
        var joinedAt = _clock.GetUtcNow();
        var joined = Progress(60);
        joined.Users.Add(lateUser);
        tracker.Progress(joined);
        _clock.Advance(120);
        var stop = Stop(180);
        stop.Users.Add(lateUser);
        var scrobbles = tracker.Stop(stop).ToDictionary(value => value.UserId);
        scrobbles[_user.Id].PlayedTicks.ShouldBe(TimeSpan.FromSeconds(180).Ticks);
        scrobbles[lateUser.Id].PlayedTicks.ShouldBe(TimeSpan.FromSeconds(120).Ticks);
        scrobbles[lateUser.Id].StartedAt.ShouldBe(joinedAt);
    }

    [Fact]
    public void AbsentParticipantDoesNotAccumulateOtherUsersListeningTime()
    {
        var tracker = new PlaybackTracker(_clock);
        var second = new User("second", "auth", "reset") { Id = Guid.NewGuid() };
        var start = Progress(0);
        start.Users.Add(second);
        tracker.Start(start);
        _clock.Advance(60);
        var firstProgress = Progress(60);
        firstProgress.Users.Add(second);
        tracker.Progress(firstProgress);
        _clock.Advance(60);
        var absent = Progress(120);
        absent.Users = [second];
        tracker.Progress(absent);
        _clock.Advance(60);
        var rejoined = Progress(180);
        rejoined.Users.Add(second);
        tracker.Progress(rejoined);
        _clock.Advance(60);
        var stop = Stop(240);
        stop.Users.Add(second);
        var scrobbles = tracker.Stop(stop).ToDictionary(value => value.UserId);
        scrobbles[_user.Id].PlayedTicks.ShouldBe(TimeSpan.FromSeconds(120).Ticks);
        scrobbles[second.Id].PlayedTicks.ShouldBe(TimeSpan.FromSeconds(240).Ticks);
    }

    [Fact]
    public void StopWithoutPlaySessionIdCanMatchAUniqueDevicePlayback()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Start(Progress(0));
        _clock.Advance(120);
        tracker.Stop(Stop(120, "")).ShouldHaveSingleItem();
    }

    [Fact]
    public void MissingSessionIdCannotChooseBetweenAmbiguousPlaybackRecords()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Start(Progress(0, session: "first"));
        tracker.Start(Progress(0, session: "second"));
        _clock.Advance(120);
        tracker.Stop(Stop(120, "")).ShouldBeEmpty();
    }

    [Fact]
    public void MissingStartBeginsCountingAtFirstObservedProgress()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Progress(Progress(100));
        _clock.Advance(20);
        tracker.Stop(Stop(120)).ShouldBeEmpty();
    }

    [Fact]
    public void ANewStartAllowsARepeatedTrackToBeScrobbledAgain()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Start(Progress(0));
        _clock.Advance(120);
        tracker.Stop(Stop(120)).ShouldHaveSingleItem();
        tracker.Start(Progress(0));
        _clock.Advance(120);
        tracker.Stop(Stop(120)).ShouldHaveSingleItem();
    }

    [Fact]
    public void AlternativeCompletionAfterStopEmitsOnceFromRetainedListeningHistory()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Start(Progress(0));
        _clock.Advance(120);
        tracker.Stop(Stop(120)).ShouldHaveSingleItem();
        var alternative = tracker.UserDataSaved(_user.Id, _track.Id).ShouldHaveSingleItem();
        alternative.AlternativeMode.ShouldBeTrue();
        alternative.PlayedTicks.ShouldBe(TimeSpan.FromSeconds(120).Ticks);
        tracker.UserDataSaved(_user.Id, _track.Id).ShouldBeEmpty();
    }

    [Fact]
    public void AlternativeCompletionBeforeStopStillRequiresEnoughListening()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Start(Progress(0));
        _clock.Advance(119);
        tracker.Progress(Progress(119));
        tracker.UserDataSaved(_user.Id, _track.Id).ShouldBeEmpty();
        _clock.Advance(1);
        var scrobbles = tracker.Stop(Stop(120));
        scrobbles.Count.ShouldBe(2);
        scrobbles.Count(scrobble => scrobble.AlternativeMode).ShouldBe(1);
        tracker.UserDataSaved(_user.Id, _track.Id).ShouldBeEmpty();
    }

    [Fact]
    public void CapacityEvictsOldestSessionAndClearRemovesRetainedState()
    {
        var tracker = new PlaybackTracker(_clock, capacity: 1);
        tracker.Start(Progress(0, session: "old"));
        _clock.Advance(1);
        tracker.Start(Progress(0, session: "new"));
        _clock.Advance(120);
        tracker.Stop(Stop(120, "old")).ShouldBeEmpty();
        tracker.Stop(Stop(120, "new")).ShouldHaveSingleItem();
        tracker.Clear();
        tracker.UserDataSaved(_user.Id, _track.Id).ShouldBeEmpty();
    }

    [Fact]
    public void OldCompletedSessionsCannotMatchNewUserDataEvents()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Start(Progress(0));
        _clock.Advance(120);
        tracker.Stop(Stop(120)).ShouldHaveSingleItem();
        _clock.Advance(6 * 60);
        tracker.UserDataSaved(_user.Id, _track.Id).ShouldBeEmpty();
    }

    private PlaybackProgressEventArgs Progress(int seconds, bool paused = false, string session = "session") => new()
    {
        Item = _track,
        Users = [_user],
        PlaySessionId = session,
        DeviceId = "device",
        IsPaused = paused,
        PlaybackPositionTicks = TimeSpan.FromSeconds(seconds).Ticks
    };

    private PlaybackStopEventArgs Stop(int seconds, string session = "session") => new()
    {
        Item = _track,
        Users = [_user],
        PlaySessionId = session,
        DeviceId = "device",
        PlaybackPositionTicks = TimeSpan.FromSeconds(seconds).Ticks
    };

}
