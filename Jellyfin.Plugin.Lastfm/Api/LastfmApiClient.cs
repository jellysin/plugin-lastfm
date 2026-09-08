using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lastfm.Models;
using Jellyfin.Plugin.Lastfm.Models.Requests;
using Jellyfin.Plugin.Lastfm.Models.Responses;
using Jellyfin.Plugin.Lastfm.Resources;
using Jellyfin.Plugin.Lastfm.Utils;
using MediaBrowser.Controller.Entities.Audio;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lastfm.Api;

public class LastfmApiClient : BaseLastfmApiClient, IDisposable
{
    private static readonly TimeSpan DuplicateScrobbleLifetime = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CapacityWarningInterval = TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, DateTimeOffset> _completedScrobbles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingScrobbles = new(StringComparer.Ordinal);
    private readonly object _scrobbleLock = new();
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly int _maxPendingScrobbles;
    private readonly int _maxCompletedScrobbles;
    private DateTimeOffset? _lastCapacityWarning;
    private bool _disposed;

    public LastfmApiClient(IHttpClientFactory httpClientFactory, ILogger logger, int maxPendingScrobbles = 128, int maxCompletedScrobbles = 4096, TimeProvider? timeProvider = null) : base(httpClientFactory, logger)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPendingScrobbles);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCompletedScrobbles);
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _maxPendingScrobbles = maxPendingScrobbles;
        _maxCompletedScrobbles = maxCompletedScrobbles;
    }

    public void Dispose()
    {
        lock (_scrobbleLock)
        {
            _disposed = true;
            _completedScrobbles.Clear();
        }
        GC.SuppressFinalize(this);
    }

    public Task<MobileSessionResponse?> RequestSession(string username, string password, CancellationToken cancellationToken = default)
    {
        var request = new MobileSessionRequest
        {
            Username = username,
            Password = password,
            ApiKey = Strings.Keys.LastfmApiKey,
            Method = Strings.Methods.GetMobileSession,
            Secure = true
        };
        return Post<MobileSessionRequest, MobileSessionResponse>(request, cancellationToken);
    }

    public async Task Scrobble(Audio item, LastfmUser user, CancellationToken cancellationToken = default, DateTimeOffset? startedAt = null)
    {
        var artist = item.Artists?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(user.SessionKey))
        {
            return;
        }
        // Include metadata when no library id is available, and separate Last.fm accounts.
        var key = $"{user.Username.ToUpperInvariant()}:{item.Id}:{artist}:{item.Name}";
        lock (_scrobbleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var now = _timeProvider.GetUtcNow();
            foreach (var expiredKey in _completedScrobbles.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
            {
                _completedScrobbles.Remove(expiredKey);
            }
            if (_completedScrobbles.ContainsKey(key) || _pendingScrobbles.Contains(key))
            {
                return;
            }
            if (_pendingScrobbles.Count >= _maxPendingScrobbles)
            {
                if (!_lastCapacityWarning.HasValue || now - _lastCapacityWarning.Value >= CapacityWarningInterval)
                {
                    _lastCapacityWarning = now;
                    _logger.LogWarning("Last.fm scrobble capacity reached; skipping excess requests");
                }
                return;
            }
            _pendingScrobbles.Add(key);
        }
        var accepted = false;
        try
        {
            var request = new ScrobbleRequest
            {
                Track = item.Name,
                Artist = artist,
                Timestamp = startedAt.HasValue ? checked((int)startedAt.Value.ToUnixTimeSeconds()) : Helpers.CurrentTimestamp(),
                Album = item.Album ?? string.Empty,
                AlbumArtist = item.AlbumArtists?.FirstOrDefault() ?? string.Empty,
                MbId = item.ProviderIds?.GetValueOrDefault("MusicBrainzTrack") ?? string.Empty,
                ApiKey = Strings.Keys.LastfmApiKey,
                Method = Strings.Methods.Scrobble,
                SessionKey = user.SessionKey,
                Secure = true
            };
            var response = await Post<ScrobbleRequest, ScrobbleResponse>(request, cancellationToken).ConfigureAwait(false);
            accepted = response is not null && !response.IsError()
                && response.Scrobbles?.Attributes?.Accepted > 0;
        }
        finally
        {
            lock (_scrobbleLock)
            {
                _pendingScrobbles.Remove(key);
                if (accepted && !_disposed)
                {
                    if (_completedScrobbles.Count >= _maxCompletedScrobbles)
                    {
                        var earliest = _completedScrobbles.MinBy(pair => pair.Value);
                        _completedScrobbles.Remove(earliest.Key);
                    }
                    _completedScrobbles[key] = _timeProvider.GetUtcNow() + DuplicateScrobbleLifetime;
                }
            }
        }
    }

    public async Task NowPlaying(Audio item, LastfmUser user, CancellationToken cancellationToken = default)
    {
        var artist = item.Artists?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(user.SessionKey))
        {
            return;
        }
        var request = new NowPlayingRequest
        {
            Track = item.Name,
            Artist = artist,
            Album = item.Album ?? string.Empty,
            AlbumArtist = item.AlbumArtists?.FirstOrDefault() ?? string.Empty,
            MbId = item.ProviderIds?.GetValueOrDefault("MusicBrainzTrack") ?? string.Empty,
            Duration = item.RunTimeTicks is > 0 ? (int)Math.Min(int.MaxValue, item.RunTimeTicks.Value / TimeSpan.TicksPerSecond) : 0,
            ApiKey = Strings.Keys.LastfmApiKey,
            Method = Strings.Methods.NowPlaying,
            SessionKey = user.SessionKey,
            Secure = true
        };
        await Post<NowPlayingRequest, ScrobbleResponse>(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> LoveTrack(Audio item, LastfmUser user, bool love = true, CancellationToken cancellationToken = default)
    {
        var artist = item.Artists?.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(item.Name) || string.IsNullOrWhiteSpace(user.SessionKey))
        {
            return false;
        }
        var request = new TrackLoveRequest
        {
            Artist = artist,
            Track = item.Name,
            ApiKey = Strings.Keys.LastfmApiKey,
            Method = love ? Strings.Methods.TrackLove : Strings.Methods.TrackUnlove,
            SessionKey = user.SessionKey,
            Secure = true
        };
        var response = await Post<TrackLoveRequest, BaseResponse>(request, cancellationToken).ConfigureAwait(false);
        return response is not null && !response.IsError();
    }

    public Task<bool> UnloveTrack(Audio item, LastfmUser user, CancellationToken cancellationToken = default)
        => LoveTrack(item, user, false, cancellationToken);

    public Task<LovedTracksResponse?> GetLovedTracks(LastfmUser user, CancellationToken cancellationToken, int page)
    {
        var request = new GetLovedTracksRequest
        {
            User = user.Username,
            ApiKey = Strings.Keys.LastfmApiKey,
            Method = Strings.Methods.GetLovedTracks,
            Limit = 1000,
            Page = page,
            Secure = true
        };
        return Get<GetLovedTracksRequest, LovedTracksResponse>(request, cancellationToken);
    }

    public Task<GetTracksResponse?> GetTracks(LastfmUser user, MusicArtist artist, CancellationToken cancellationToken)
    {
        var request = new GetTracksRequest
        {
            User = user.Username,
            Artist = artist.Name,
            ApiKey = Strings.Keys.LastfmApiKey,
            Method = Strings.Methods.GetTracks,
            Limit = 1000,
            Secure = true
        };
        return Get<GetTracksRequest, GetTracksResponse>(request, cancellationToken);
    }

    public Task<GetTracksResponse?> GetTracks(LastfmUser user, CancellationToken cancellationToken, int page = 0, int limit = 200)
    {
        var request = new GetTracksRequest
        {
            User = user.Username,
            ApiKey = Strings.Keys.LastfmApiKey,
            Method = Strings.Methods.GetTracks,
            Limit = limit,
            Page = page,
            Secure = true
        };
        return Get<GetTracksRequest, GetTracksResponse>(request, cancellationToken);
    }
}
