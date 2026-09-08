using JellySin.Plugin.Lastfm.Playback;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class PlaybackTests
{
    private readonly TestClock _clock = new();
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
    public void SyntheticProgressCannotCreateAListen()
    {
        var tracker = new PlaybackTracker(_clock);
        tracker.Observe(Snapshot(PlaybackSignal.Start, 0));
        for (var second = 10; second <= 100; second += 10)
        {
            _clock.Advance(10);
            Assert.Null(tracker.Observe(Snapshot(PlaybackSignal.Progress, second) with { Automated = true }).Scrobble);
        }
        Assert.Null(tracker.Observe(Snapshot(PlaybackSignal.Stop, 100)).Scrobble);
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

    private PlaybackSnapshot Snapshot(PlaybackSignal signal, int position)
        => new("session", _user, _track, signal, TimeSpan.FromSeconds(position).Ticks, false, false, _clock.GetTimestamp(), _clock.GetUtcNow());
}
