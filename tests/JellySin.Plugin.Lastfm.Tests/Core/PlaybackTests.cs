using JellySin.Plugin.Lastfm.Playback;
using Microsoft.Extensions.Time.Testing;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class PlaybackTests
{
    private readonly FakeTimeProvider _clock = TestTime.Clock();
    private readonly Guid _user = Guid.NewGuid();
    private readonly MusicTrack _track = new(Guid.NewGuid(), "Artist", "Track", "Album", null, 100);

    [Fact]
    public void OlderEventsCannotRewindAnObservationAndDoubleCountListening()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Observe(Snapshot(PlaybackSignal.Start, 0));
        _clock.Advance(20);
        var delayed = Snapshot(PlaybackSignal.Progress, 20);
        _clock.Advance(10);
        tracker.Observe(Snapshot(PlaybackSignal.Progress, 30));
        tracker.Observe(delayed);
        _clock.Advance(10);
        Assert.Null(tracker.Observe(Snapshot(PlaybackSignal.Progress, 40)).Scrobble);
    }

    [Fact]
    public void EligibilityRemainsInRecoveryBufferAfterStopUntilDurableAcknowledgement()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Observe(Snapshot(PlaybackSignal.Start, 0));
        _clock.Advance(30);
        tracker.Observe(Snapshot(PlaybackSignal.Progress, 30));
        _clock.Advance(30);
        var listen = tracker.Observe(Snapshot(PlaybackSignal.Stop, 60)).Scrobble;
        Assert.NotNull(listen);
        Assert.Equal(listen, Assert.Single(tracker.Pending()));
        Assert.False(tracker.IsCurrent(listen));
        tracker.Acknowledge(listen.OccurrenceId);
        Assert.Empty(tracker.Pending());
    }

    [Fact]
    public void AccountGenerationChangeCannotInheritPreviouslyHeardSeconds()
    {
        var tracker = new PlaybackTracker(_clock);
        var first = Guid.NewGuid();
        tracker.Observe(Snapshot(PlaybackSignal.Start, 0) with { AccountGeneration = first });
        _clock.Advance(30);
        tracker.Observe(Snapshot(PlaybackSignal.Progress, 30) with { AccountGeneration = first });
        _clock.Advance(30);
        Assert.Null(tracker.Observe(Snapshot(PlaybackSignal.Progress, 60) with { AccountGeneration = Guid.NewGuid() }).Scrobble);
        Assert.Empty(tracker.Pending());
    }

    [Fact]
    public void ObservedHalfDurationQualifiesExactlyOnce()
    {
        var tracker = new PlaybackTracker(_clock);
        Assert.NotNull(tracker.Observe(Snapshot(PlaybackSignal.Start, 0)).NowPlaying);
        for (var second = 10; second <= 60; second += 10)
        {
            _clock.Advance(10);
            var result = tracker.Observe(Snapshot(PlaybackSignal.Progress, second));
            Assert.Equal(second == 50, result.Scrobble is not null);
        }
        Assert.Null(tracker.Observe(Snapshot(PlaybackSignal.Stop, 60)).Scrobble);
    }

    [Fact]
    public void SeekingForwardDoesNotCountSkippedTime()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Observe(Snapshot(PlaybackSignal.Start, 0));
        _clock.Advance(5);
        Assert.Null(tracker.Observe(Snapshot(PlaybackSignal.Progress, 80)).Scrobble);
        _clock.Advance(20);
        Assert.Null(tracker.Observe(Snapshot(PlaybackSignal.Stop, 100)).Scrobble);
    }

    [Fact]
    public void PauseDurationDoesNotCountAsListening()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Observe(Snapshot(PlaybackSignal.Start, 0));
        _clock.Advance(20);
        tracker.Observe(Snapshot(PlaybackSignal.Progress, 20) with { Paused = true });
        _clock.Advance(30);
        Assert.Null(tracker.Observe(Snapshot(PlaybackSignal.Progress, 20)).Scrobble);
        _clock.Advance(20);
        Assert.Null(tracker.Observe(Snapshot(PlaybackSignal.Stop, 40)).Scrobble);
    }

    [Fact]
    public void ServerTicksBetweenStartAndStopCreateAListen()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Observe(Snapshot(PlaybackSignal.Start, 0));
        Assert.Null(ServerTicks(tracker, 1, 60));
        Assert.NotNull(tracker.Observe(Snapshot(PlaybackSignal.Stop, 60)).Scrobble);
        Assert.Single(tracker.Pending());
    }

    [Fact]
    public void ServerTicksWithoutAStopCreateNoListen()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Observe(Snapshot(PlaybackSignal.Start, 0));
        Assert.Null(ServerTicks(tracker, 1, 99));
        Assert.Empty(tracker.Pending());
    }

    [Fact]
    public void ServerTicksAfterClientProgressAreIgnored()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Observe(Snapshot(PlaybackSignal.Start, 0));
        _clock.Advance(10);
        tracker.Observe(Snapshot(PlaybackSignal.Progress, 10));
        Assert.Null(ServerTicks(tracker, 11, 70));
        Assert.Null(tracker.Observe(Snapshot(PlaybackSignal.Stop, 70)).Scrobble);
    }

    [Fact]
    public void ServerTicksCountAcrossAPauseAndResume()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Observe(Snapshot(PlaybackSignal.Start, 0));
        Assert.Null(ServerTicks(tracker, 1, 46));
        tracker.Observe(Snapshot(PlaybackSignal.Progress, 46) with { Paused = true });
        _clock.Advance(30);
        tracker.Observe(Snapshot(PlaybackSignal.Progress, 46));
        Assert.Null(ServerTicks(tracker, 47, 93));
        Assert.NotNull(tracker.Observe(Snapshot(PlaybackSignal.Stop, 93)).Scrobble);
    }

    [Fact]
    public void ServerTicksCountOnlyUpToTheStopPosition()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Observe(Snapshot(PlaybackSignal.Start, 0));
        ServerTicks(tracker, 1, 60);
        Assert.Null(tracker.Observe(Snapshot(PlaybackSignal.Stop, 20)).Scrobble);
    }

    [Fact]
    public void ServerTicksNeverAddToHeardTime()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Observe(Snapshot(PlaybackSignal.Start, 0));
        ServerTicks(tracker, 1, 30);
        Assert.Null(tracker.Observe(Snapshot(PlaybackSignal.Stop, 30)).Scrobble);
    }

    [Fact]
    public void RepeatCreatesNewOccurrence()
    {
        var tracker = new PlaybackTracker(_clock);
        var first = tracker.Observe(Snapshot(PlaybackSignal.Start, 0)).NowPlaying;
        _clock.Advance(30);
        tracker.Observe(Snapshot(PlaybackSignal.Progress, 30));
        _clock.Advance(30);
        tracker.Observe(Snapshot(PlaybackSignal.Stop, 60));
        var second = tracker.Observe(Snapshot(PlaybackSignal.Start, 0)).NowPlaying;
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.OccurrenceId, second.OccurrenceId);
    }

    [Fact]
    public void DuplicateStartDoesNotResetObservedTime()
    {
        var tracker = new PlaybackTracker(_clock);
        var first = tracker.Observe(Snapshot(PlaybackSignal.Start, 0)).NowPlaying;
        Assert.Null(tracker.Observe(Snapshot(PlaybackSignal.Start, 0)).NowPlaying);
        _clock.Advance(25);
        tracker.Observe(Snapshot(PlaybackSignal.Progress, 25));
        _clock.Advance(25);
        Assert.Equal(first!.OccurrenceId, tracker.Observe(Snapshot(PlaybackSignal.Progress, 50)).Scrobble!.OccurrenceId);
    }

    [Fact]
    public void ConcurrentSessionsDoNotShareProgress()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Observe(Snapshot(PlaybackSignal.Start, 0));
        tracker.Observe(Snapshot(PlaybackSignal.Start, 0) with { SessionId = "other" });
        _clock.Advance(30);
        tracker.Observe(Snapshot(PlaybackSignal.Progress, 30));
        _clock.Advance(30);
        Assert.Null(tracker.Observe(Snapshot(PlaybackSignal.Stop, 30) with { SessionId = "other" }).Scrobble);
        Assert.NotNull(tracker.Observe(Snapshot(PlaybackSignal.Stop, 60)).Scrobble);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(-1)]
    public void TracksAtMostThirtySecondsAreIneligible(int duration)
    {
        var tracker = new PlaybackTracker(_clock);
        Assert.Null(tracker.Observe(Snapshot(PlaybackSignal.Start, 0) with { Track = _track with { DurationSeconds = duration } }).NowPlaying);
    }

    [Fact]
    public void SessionCapRefusesANewSessionUntilIdleSessionsPassFifteenMinutes()
    {
        var tracker = new PlaybackTracker(_clock);
        var listens = Enumerable.Range(0, 1024)
            .Select(index => tracker.Observe(Snapshot(PlaybackSignal.Start, 0) with { SessionId = $"session-{index}" }).NowPlaying!)
            .ToArray();
        Assert.Throws<InvalidOperationException>(() => tracker.Observe(Snapshot(PlaybackSignal.Start, 0) with { SessionId = "one more" }));
        _clock.Advance(10);
        tracker.Observe(Snapshot(PlaybackSignal.Progress, 10) with { SessionId = "session-0" });
        _clock.Advance(890);
        Assert.Throws<InvalidOperationException>(() => tracker.Observe(Snapshot(PlaybackSignal.Start, 0) with { SessionId = "one more" }));
        _clock.Advance(1);
        Assert.NotNull(tracker.Observe(Snapshot(PlaybackSignal.Start, 0) with { SessionId = "one more" }).NowPlaying);
        Assert.True(tracker.IsCurrent(listens[0]));
        Assert.False(tracker.IsCurrent(listens[1]));
    }

    /// <summary>The server's automated progress: one tick a second from <paramref name="first"/> to <paramref name="last"/>.</summary>
    private EligibleListen? ServerTicks(PlaybackTracker tracker, int first, int last)
    {
        EligibleListen? scrobble = null;
        for (var second = first; second <= last; second++)
        {
            _clock.Advance(1);
            scrobble ??= tracker.Observe(Snapshot(PlaybackSignal.Progress, second) with { Automated = true }).Scrobble;
        }
        return scrobble;
    }

    private PlaybackSnapshot Snapshot(PlaybackSignal signal, int position)
        => new("session", _user, _track, signal, TimeSpan.FromSeconds(position).Ticks, false, false, _clock.GetTimestamp(), _clock.GetUtcNow());
}
