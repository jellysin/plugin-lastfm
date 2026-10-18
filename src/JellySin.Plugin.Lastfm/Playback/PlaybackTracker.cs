using System.Collections.Concurrent;
using JellySin.Plugin.Lastfm.Configuration;

namespace JellySin.Plugin.Lastfm.Playback;

public enum PlaybackSignal { Start, Progress, Stop }

public sealed record MusicTrack(Guid ItemId, string Artist, string Title, string? Album, string? MusicBrainzId, double DurationSeconds,
    string? AlbumArtist = null);

public sealed record PlaybackSnapshot(string SessionId, Guid UserId, MusicTrack Track, PlaybackSignal Signal, long PositionTicks, bool Paused, bool Automated, long MonotonicTimestamp, DateTimeOffset UtcTimestamp, Guid AccountGeneration = default, Guid CaptureGeneration = default, long PluginGeneration = default,
    ScrobbleThreshold? Threshold = null);

public sealed record EligibleListen(Guid OccurrenceId, Guid UserId, MusicTrack Track, DateTimeOffset StartedAt, Guid AccountGeneration = default, Guid CaptureGeneration = default, long PluginGeneration = default);

public sealed record PlaybackUpdate(EligibleListen? NowPlaying, EligibleListen? Scrobble);

/// <summary>Counts only progress confirmed by both monotonic time and track-position movement.</summary>
/// <remarks>
/// Some clients report only the start and the stop of a track. For a session that sent no progress of its own since the
/// start, the server's automated progress ticks count instead, and only once the client's stop confirms the position.
/// A report that only pauses or resumes the track does not count as progress of the client's own: the server does not
/// say which player event caused a report, so a change of the paused state is what tells such a report apart.
/// </remarks>
public sealed class PlaybackTracker(TimeProvider clock)
{
    private const int MaxSessions = 1024;
    private readonly ConcurrentDictionary<(string Session, Guid User), Observation> _active = new();
    private readonly ConcurrentDictionary<Guid, EligibleListen> _pending = new();

    public IReadOnlyList<EligibleListen> Pending() => _pending.Values.ToArray();
    public int PendingCount(Guid userId) => _pending.Values.Count(item => item.UserId == userId);
    public void Acknowledge(Guid occurrenceId) => _pending.TryRemove(occurrenceId, out _);
    public bool IsCurrent(EligibleListen listen) => _active.Values.Any(item => item.Listen.OccurrenceId == listen.OccurrenceId);

    public PlaybackUpdate Observe(PlaybackSnapshot value)
    {
        if (value.UserId == Guid.Empty || !(value.Threshold ?? ScrobbleThreshold.Lastfm).Counts(value.Track.DurationSeconds)
            || string.IsNullOrWhiteSpace(value.Track.Artist) || string.IsNullOrWhiteSpace(value.Track.Title)) return new(null, null);
        var key = (value.SessionId, value.UserId);
        if (value.Automated) { ObserveAutomated(key, value); return new(null, null); }
        _active.TryGetValue(key, out var observation);
        if (observation is not null && (observation.Listen.AccountGeneration != value.AccountGeneration || observation.Listen.CaptureGeneration != value.CaptureGeneration
            || observation.Listen.PluginGeneration != value.PluginGeneration))
        {
            _active.TryRemove(key, out _);
            observation = null;
        }
        if (observation is not null && value.MonotonicTimestamp < observation.Timestamp) return new(null, null);
        if (value.Signal == PlaybackSignal.Start) return ObserveStart(key, value, observation);
        if (observation is null || observation.Listen.Track.ItemId != value.Track.ItemId) return new(null, null);
        var elapsed = clock.GetElapsedTime(observation.Timestamp, value.MonotonicTimestamp).TotalSeconds;
        var movement = (value.PositionTicks - observation.PositionTicks) / (double)TimeSpan.TicksPerSecond;
        if (!observation.Paused && elapsed is > 0 and <= 45 && movement > 0 && movement <= elapsed + 2)
            observation.HeardSeconds += Math.Min(elapsed, movement);
        if (value.Signal == PlaybackSignal.Progress && value.Paused == observation.Paused) observation.ClientProgress = true;
        else if (value.Signal == PlaybackSignal.Stop) CreditAutomated(observation, value.PositionTicks);
        observation.Timestamp = value.MonotonicTimestamp;
        observation.PositionTicks = value.PositionTicks;
        observation.Paused = value.Paused;
        EligibleListen? eligible = null;
        if (!observation.Submitted && observation.HeardSeconds >= observation.Threshold.RequiredSeconds(observation.Listen.Track.DurationSeconds))
        {
            if (_pending.Count >= MaxSessions) throw new InvalidOperationException("Pending listening recovery capacity reached.");
            _pending.TryAdd(observation.Listen.OccurrenceId, observation.Listen);
            observation.Submitted = true;
            eligible = observation.Listen;
        }
        if (value.Signal == PlaybackSignal.Stop) _active.TryRemove(key, out _);
        return new(null, eligible);
    }

    public void Reset() { _active.Clear(); _pending.Clear(); }

    private PlaybackUpdate ObserveStart((string Session, Guid User) key, PlaybackSnapshot value, Observation? observation)
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
        var listen = new EligibleListen(Guid.NewGuid(), value.UserId, value.Track, value.UtcTimestamp, value.AccountGeneration, value.CaptureGeneration, value.PluginGeneration);
        _active[key] = new Observation(listen, value.MonotonicTimestamp, value.PositionTicks, value.Paused, value.Threshold ?? ScrobbleThreshold.Lastfm);
        return new(value.Paused ? null : listen, null);
    }

    private void ObserveAutomated((string Session, Guid User) key, PlaybackSnapshot value)
    {
        if (value.Signal != PlaybackSignal.Progress || !_active.TryGetValue(key, out var observation) || observation.ClientProgress
            || observation.Listen.Track.ItemId != value.Track.ItemId || observation.Listen.AccountGeneration != value.AccountGeneration
            || observation.Listen.CaptureGeneration != value.CaptureGeneration || observation.Listen.PluginGeneration != value.PluginGeneration
            || value.MonotonicTimestamp <= observation.AutomatedTimestamp) return;
        var elapsed = clock.GetElapsedTime(observation.AutomatedTimestamp, value.MonotonicTimestamp).TotalSeconds;
        var movement = (value.PositionTicks - observation.AutomatedPositionTicks) / (double)TimeSpan.TicksPerSecond;
        if (!value.Paused && elapsed <= 45 && movement > 0 && movement <= elapsed + 2)
            observation.AutomatedSeconds += Math.Min(elapsed, movement);
        observation.AutomatedTimestamp = value.MonotonicTimestamp;
        observation.AutomatedPositionTicks = value.PositionTicks;
    }

    /// <summary>The server's ticks count up to the distance the client's stop reports, and never on top of heard time.</summary>
    private static void CreditAutomated(Observation observation, long stopPositionTicks)
    {
        if (observation.ClientProgress) return;
        var reported = (stopPositionTicks - observation.StartPositionTicks) / (double)TimeSpan.TicksPerSecond + 2;
        observation.HeardSeconds = Math.Max(observation.HeardSeconds, Math.Min(observation.AutomatedSeconds, reported));
    }

    private void RemoveStale(long timestamp)
    {
        foreach (var key in _active.Where(pair => clock.GetElapsedTime(pair.Value.Timestamp, timestamp) > TimeSpan.FromMinutes(15)).Select(pair => pair.Key).ToArray())
            _active.TryRemove(key, out _);
    }

    // The threshold in force when the track started applies to the whole play, even when an administrator changes it meanwhile.
    private sealed class Observation(EligibleListen listen, long timestamp, long positionTicks, bool paused, ScrobbleThreshold threshold)
    {
        public EligibleListen Listen { get; } = listen;
        public ScrobbleThreshold Threshold { get; } = threshold;
        public long Timestamp { get; set; } = timestamp;
        public long PositionTicks { get; set; } = positionTicks;
        public bool Paused { get; set; } = paused;
        public double HeardSeconds { get; set; }
        public bool Submitted { get; set; }
        public long StartPositionTicks { get; } = positionTicks;
        public bool ClientProgress { get; set; }
        public long AutomatedTimestamp { get; set; } = timestamp;
        public long AutomatedPositionTicks { get; set; } = positionTicks;
        public double AutomatedSeconds { get; set; }
    }
}
