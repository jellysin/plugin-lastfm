namespace JellySin.Plugin.Lastfm.Playback;

public enum PlaybackSignal { Start, Progress, Stop }

public sealed record MusicTrack(Guid ItemId, string Artist, string Title, string? Album, string? MusicBrainzId, double DurationSeconds);

public sealed record PlaybackSnapshot(string SessionId, Guid UserId, MusicTrack Track, PlaybackSignal Signal, long PositionTicks, bool Paused, bool Automated, long MonotonicTimestamp, DateTimeOffset UtcTimestamp);

public sealed record EligibleListen(Guid OccurrenceId, Guid UserId, MusicTrack Track, DateTimeOffset StartedAt);

public sealed record PlaybackUpdate(EligibleListen? NowPlaying, EligibleListen? Scrobble);

/// <summary>Counts only progress confirmed by both monotonic time and track-position movement.</summary>
public sealed class PlaybackTracker(TimeProvider clock)
{
    private const int MaxSessions = 1024;
    private readonly Dictionary<(string Session, Guid User), Observation> _active = [];

    public PlaybackUpdate Observe(PlaybackSnapshot value)
    {
        if (value.Automated || value.UserId == Guid.Empty || value.Track.DurationSeconds <= 30
            || string.IsNullOrWhiteSpace(value.Track.Artist) || string.IsNullOrWhiteSpace(value.Track.Title)) return new(null, null);
        var key = (value.SessionId, value.UserId);
        _active.TryGetValue(key, out var observation);
        if (value.Signal == PlaybackSignal.Start)
        {
            if (observation is not null && observation.Listen.Track.ItemId == value.Track.ItemId
                && !(value.PositionTicks <= TimeSpan.TicksPerSecond && observation.PositionTicks > TimeSpan.TicksPerSecond * 2)
                && clock.GetElapsedTime(observation.Timestamp, value.MonotonicTimestamp) < TimeSpan.FromMinutes(15))
            {
                observation.Timestamp = value.MonotonicTimestamp;
                observation.PositionTicks = value.PositionTicks;
                observation.Paused = value.Paused;
                return new(null, null);
            }
            if (_active.Count >= MaxSessions && observation is null) RemoveStale(value.MonotonicTimestamp);
            if (_active.Count >= MaxSessions && observation is null) throw new InvalidOperationException("Playback tracking capacity reached.");
            var listen = new EligibleListen(Guid.NewGuid(), value.UserId, value.Track, value.UtcTimestamp);
            _active[key] = new Observation(listen, value.MonotonicTimestamp, value.PositionTicks, value.Paused);
            return new(value.Paused ? null : listen, null);
        }
        if (observation is null || observation.Listen.Track.ItemId != value.Track.ItemId) return new(null, null);
        var elapsed = clock.GetElapsedTime(observation.Timestamp, value.MonotonicTimestamp).TotalSeconds;
        var movement = (value.PositionTicks - observation.PositionTicks) / (double)TimeSpan.TicksPerSecond;
        if (!observation.Paused && elapsed is > 0 and <= 45 && movement > 0 && movement <= elapsed + 2)
            observation.HeardSeconds += Math.Min(elapsed, movement);
        observation.Timestamp = value.MonotonicTimestamp;
        observation.PositionTicks = value.PositionTicks;
        observation.Paused = value.Paused;
        EligibleListen? eligible = null;
        if (!observation.Submitted && observation.HeardSeconds >= Math.Min(value.Track.DurationSeconds / 2, 240))
        {
            observation.Submitted = true;
            eligible = observation.Listen;
        }
        if (value.Signal == PlaybackSignal.Stop) _active.Remove(key);
        return new(null, eligible);
    }

    public void Reset() => _active.Clear();

    private void RemoveStale(long timestamp)
    {
        foreach (var key in _active.Where(pair => clock.GetElapsedTime(pair.Value.Timestamp, timestamp) > TimeSpan.FromMinutes(15)).Select(pair => pair.Key).ToArray())
            _active.Remove(key);
    }

    private sealed class Observation(EligibleListen listen, long timestamp, long positionTicks, bool paused)
    {
        public EligibleListen Listen { get; } = listen;
        public long Timestamp { get; set; } = timestamp;
        public long PositionTicks { get; set; } = positionTicks;
        public bool Paused { get; set; } = paused;
        public double HeardSeconds { get; set; }
        public bool Submitted { get; set; }
    }
}
