using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.Lastfm.Utils;

public sealed record PlaybackScrobble(Guid UserId, Audio Item, DateTimeOffset StartedAt, long PlayedTicks, bool AlternativeMode);

/// <summary>
/// Tracks observed playback rather than assuming a media position is listening time.
/// </summary>
public sealed class PlaybackTracker
{
    private readonly TimeProvider _timeProvider;
    private readonly int _capacity;
    private readonly object _lock = new();
    private readonly Dictionary<(string Session, Guid ItemId), Playback> _playbacks = [];
    private long _activity;

    public PlaybackTracker(TimeProvider? timeProvider = null, int capacity = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _capacity = capacity;
    }

    public void Start(PlaybackProgressEventArgs args)
    {
        if (args.Item is not Audio item)
        {
            return;
        }
        lock (_lock)
        {
            Prune();
            Add(args, item);
        }
    }

    public void Progress(PlaybackProgressEventArgs args)
    {
        // Jellyfin extrapolates automatic progress even after a client stops checking in.
        // Only client reports can establish listening time or advance its baseline.
        if (args.IsAutomated || args.Item is not Audio item)
        {
            return;
        }
        lock (_lock)
        {
            Prune();
            var playback = Find(args);
            if (playback is not null)
            {
                if (!playback.Stopped)
                {
                    Update(playback, args);
                }
            }
            else
            {
                // When startup was missed, only credit playback observed from this point.
                Add(args, item);
            }
        }
    }

    public IReadOnlyList<PlaybackScrobble> Stop(PlaybackStopEventArgs args)
    {
        lock (_lock)
        {
            Prune();
            if (args.Item is not Audio)
            {
                return [];
            }
            var playback = Find(args);
            if (playback is null)
            {
                return [];
            }
            if (!playback.Stopped)
            {
                // Older servers report an extrapolated position when cleaning up an idle
                // client. Preserve observed listening, but do not credit that final interval.
                var trustFinalPosition = args.Session is null || args.Session.LastPlaybackCheckIn == default
                    || _timeProvider.GetUtcNow().UtcDateTime - args.Session.LastPlaybackCheckIn <= TimeSpan.FromMinutes(5);
                Update(playback, args, trustFinalPosition);
                playback.Stopped = true;
            }
            var candidates = new List<PlaybackScrobble>();
            foreach (var userId in playback.Participants.Keys)
            {
                Emit(playback, userId, false, candidates);
                if (playback.SavedUsers.Contains(userId))
                {
                    Emit(playback, userId, true, candidates);
                }
            }
            return candidates;
        }
    }

    public IReadOnlyList<PlaybackScrobble> UserDataSaved(Guid userId, Guid itemId)
    {
        lock (_lock)
        {
            Prune();
            var candidates = new List<PlaybackScrobble>();
            foreach (var playback in _playbacks.Values.Where(playback => playback.Item.Id == itemId && playback.Participants.ContainsKey(userId)))
            {
                playback.SavedUsers.Add(userId);
                // UserDataSaved contains no reliable listening position. Use observed progress;
                // if the final interval matters, Stop will retry eligibility after accounting it.
                Emit(playback, userId, true, candidates);
            }
            return candidates;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _playbacks.Clear();
        }
    }

    private void Add(PlaybackProgressEventArgs args, Audio item)
    {
        var key = GetKey(args);
        if (!_playbacks.ContainsKey(key) && _playbacks.Count >= _capacity)
        {
            _playbacks.Remove(_playbacks.MinBy(pair => pair.Value.Activity).Key);
        }
        _playbacks[key] = new Playback
        {
            Item = item,
            ActiveUsers = args.Users.Select(user => user.Id).ToHashSet(),
            Participants = args.Users.Select(user => user.Id).Distinct().ToDictionary(id => id, _ => new Participant(_timeProvider.GetUtcNow())),
            PlaySessionId = args.PlaySessionId,
            SessionId = args.Session?.Id,
            DeviceId = args.DeviceId,
            LastTimestamp = _timeProvider.GetTimestamp(),
            LastPosition = args.PlaybackPositionTicks,
            Paused = args.IsPaused,
            Activity = ++_activity
        };
    }

    private void Update(Playback playback, PlaybackProgressEventArgs args, bool creditPosition = true)
    {
        var now = _timeProvider.GetTimestamp();
        var elapsed = Math.Max(0, _timeProvider.GetElapsedTime(playback.LastTimestamp, now).Ticks);
        long credit = 0;
        if (creditPosition && !playback.Paused && playback.LastPosition is >= 0 && args.PlaybackPositionTicks is >= 0
            && args.PlaybackPositionTicks > playback.LastPosition)
        {
            var advanced = args.PlaybackPositionTicks.Value - playback.LastPosition.Value;
            // A seek cannot manufacture time, and a stalled position cannot accrue time.
            credit = Math.Min(advanced, elapsed);
        }
        var users = args.Users.Select(user => user.Id).ToHashSet();
        foreach (var userId in users)
        {
            if (!playback.Participants.TryGetValue(userId, out var participant))
            {
                playback.Participants[userId] = new Participant(_timeProvider.GetUtcNow());
            }
            else if (playback.ActiveUsers.Contains(userId))
            {
                participant.PlayedTicks += Math.Min(credit, long.MaxValue - participant.PlayedTicks);
            }
        }
        playback.LastPosition = args.PlaybackPositionTicks;
        playback.LastTimestamp = now;
        playback.Paused = args.IsPaused;
        playback.Activity = ++_activity;
        playback.ActiveUsers = users;
        if (string.IsNullOrEmpty(playback.PlaySessionId))
        {
            playback.PlaySessionId = args.PlaySessionId;
        }
        if (string.IsNullOrEmpty(playback.SessionId))
        {
            playback.SessionId = args.Session?.Id;
        }
        if (string.IsNullOrEmpty(playback.DeviceId))
        {
            playback.DeviceId = args.DeviceId;
        }
    }

    private static void Emit(Playback playback, Guid userId, bool alternativeMode, List<PlaybackScrobble> candidates)
    {
        var participant = playback.Participants[userId];
        if (ScrobbleEligibility.IsEligible(playback.Item.RunTimeTicks, participant.PlayedTicks)
            && playback.Emitted.Add((userId, alternativeMode)))
        {
            candidates.Add(new PlaybackScrobble(userId, playback.Item, participant.StartedAt, participant.PlayedTicks, alternativeMode));
        }
    }

    private void Prune()
    {
        var now = _timeProvider.GetTimestamp();
        foreach (var key in _playbacks.Where(pair => _timeProvider.GetElapsedTime(pair.Value.LastTimestamp, now)
            > (pair.Value.Stopped ? TimeSpan.FromMinutes(5) : TimeSpan.FromHours(24))).Select(pair => pair.Key).ToArray())
        {
            _playbacks.Remove(key);
        }
    }

    private static (string Session, Guid ItemId) GetKey(PlaybackProgressEventArgs args)
    {
        var session = !string.IsNullOrWhiteSpace(args.PlaySessionId) ? "play:" + args.PlaySessionId
            : !string.IsNullOrWhiteSpace(args.Session?.Id) ? "session:" + args.Session.Id
            : !string.IsNullOrWhiteSpace(args.DeviceId) ? "device:" + args.DeviceId
            : "users:" + string.Join(',', args.Users.Select(user => user.Id).Order());
        return (session, args.Item.Id);
    }

    private Playback? Find(PlaybackProgressEventArgs args)
    {
        if (_playbacks.TryGetValue(GetKey(args), out var exact))
        {
            return exact;
        }
        // Some clients omit PlaySessionId on subsequent reports. Only accept an
        // unambiguous match; never merge two distinct, explicitly identified plays.
        var matches = _playbacks.Values.Where(playback => playback.Item.Id == args.Item.Id
            && (string.IsNullOrEmpty(args.PlaySessionId) || string.IsNullOrEmpty(playback.PlaySessionId))
            && (!string.IsNullOrEmpty(args.Session?.Id) ? args.Session.Id == playback.SessionId
                : !string.IsNullOrEmpty(args.DeviceId) ? args.DeviceId == playback.DeviceId
                : playback.ActiveUsers.SetEquals(args.Users.Select(user => user.Id)))).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private sealed class Playback
    {
        public required Audio Item { get; init; }
        public required HashSet<Guid> ActiveUsers { get; set; }
        public required Dictionary<Guid, Participant> Participants { get; init; }
        public string? PlaySessionId { get; set; }
        public string? SessionId { get; set; }
        public string? DeviceId { get; set; }
        public long LastTimestamp { get; set; }
        public long? LastPosition { get; set; }
        public long Activity { get; set; }
        public bool Paused { get; set; }
        public bool Stopped { get; set; }
        public HashSet<Guid> SavedUsers { get; } = [];
        public HashSet<(Guid UserId, bool AlternativeMode)> Emitted { get; } = [];
    }

    private sealed class Participant(DateTimeOffset startedAt)
    {
        public DateTimeOffset StartedAt { get; } = startedAt;
        public long PlayedTicks { get; set; }
    }
}
