using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lastfm.Api;
using Jellyfin.Plugin.Lastfm.Utils;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lastfm;

public class ServerEntryPoint : IHostedService, IDisposable
{
    private const int MaxPendingOperations = 256;
    private static readonly TimeSpan CapacityWarningInterval = TimeSpan.FromMinutes(1);
    private readonly ISessionManager _sessionManager;
    private readonly IUserDataManager _userDataManager;
    private readonly LastfmApiClient _apiClient;
    private readonly PlaybackTracker _playbackTracker;
    private readonly ILogger<ServerEntryPoint> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _concurrency = new(4, 4);
    private readonly HashSet<Task> _pending = [];
    private readonly object _lifecycleLock = new();
    private Task? _shutdownTask;
    private DateTimeOffset? _lastCapacityWarning;
    private bool _started;
    private bool _disposed;

    public static ServerEntryPoint? Instance { get; private set; }

    public ServerEntryPoint(ISessionManager sessionManager, IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory, IUserDataManager userDataManager, TimeProvider? timeProvider = null)
    {
        _logger = loggerFactory.CreateLogger<ServerEntryPoint>();
        _sessionManager = sessionManager;
        _userDataManager = userDataManager;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _apiClient = new LastfmApiClient(httpClientFactory, _logger, timeProvider: _timeProvider);
        _playbackTracker = new PlaybackTracker(_timeProvider);
        Instance = this;
    }

    private void PlaybackStart(object? sender, PlaybackProgressEventArgs args)
    {
        lock (_lifecycleLock)
        {
            if (!_started || args.Item is not Audio item)
            {
                return;
            }
            _playbackTracker.Start(args);
            Queue(async token =>
            {
                foreach (var user in args.Users)
                {
                    var configured = UserHelpers.GetUser(user);
                    if (configured is { Options.Scrobble: true } && !string.IsNullOrWhiteSpace(configured.SessionKey))
                    {
                        await _apiClient.NowPlaying(item, configured, token).ConfigureAwait(false);
                    }
                }
            });
        }
    }

    private void PlaybackStopped(object? sender, PlaybackStopEventArgs args)
    {
        lock (_lifecycleLock)
        {
            if (_started)
            {
                Submit(_playbackTracker.Stop(args));
            }
        }
    }

    private void PlaybackProgress(object? sender, PlaybackProgressEventArgs args)
    {
        lock (_lifecycleLock)
        {
            if (_started)
            {
                _playbackTracker.Progress(args);
            }
        }
    }

    private void Submit(IReadOnlyList<PlaybackScrobble> candidates)
    {
        foreach (var candidate in candidates)
        {
            var configured = UserHelpers.GetUser(candidate.UserId);
            if (configured is { Options.Scrobble: true } && configured.Options.AlternativeMode == candidate.AlternativeMode
                && !string.IsNullOrWhiteSpace(configured.SessionKey))
            {
                Queue(token => _apiClient.Scrobble(candidate.Item, configured, token, candidate.StartedAt));
            }
        }
    }

    private void UserDataSaved(object? sender, UserDataSaveEventArgs args)
    {
        lock (_lifecycleLock)
        {
            if (!_started || args.Item is not Audio item)
            {
                return;
            }
            var configured = UserHelpers.GetUser(args.UserId);
            if (configured?.Options is null || string.IsNullOrWhiteSpace(configured.SessionKey))
            {
                return;
            }
            if (args.SaveReason == UserDataSaveReason.UpdateUserRating && configured.Options.SyncFavourites && !Plugin.Syncing)
            {
                Queue(token => _apiClient.LoveTrack(item, configured, args.UserData.IsFavorite, token));
            }
            else if (args.SaveReason == UserDataSaveReason.PlaybackFinished && configured.Options.Scrobble && configured.Options.AlternativeMode)
            {
                Submit(_playbackTracker.UserDataSaved(args.UserId, item.Id));
            }
        }
    }

    private void Queue(Func<CancellationToken, Task> operation)
    {
        lock (_lifecycleLock)
        {
            if (!_started || _disposed)
            {
                return;
            }
            if (_pending.Count >= MaxPendingOperations)
            {
                var now = _timeProvider.GetUtcNow();
                if (!_lastCapacityWarning.HasValue || now - _lastCapacityWarning.Value >= CapacityWarningInterval)
                {
                    _lastCapacityWarning = now;
                    _logger.LogWarning("Last.fm playback event capacity reached; skipping excess events");
                }
                return;
            }
            var token = _lifetime.Token;
            var task = Task.Run(async () =>
            {
                var acquired = false;
                try
                {
                    await _concurrency.WaitAsync(token).ConfigureAwait(false);
                    acquired = true;
                    token.ThrowIfCancellationRequested();
                    await operation(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch (Exception exception)
                {
                    _logger.LogError("Last.fm playback event failed ({FailureType})", exception.GetType().Name);
                }
                finally
                {
                    if (acquired)
                    {
                        _concurrency.Release();
                    }
                }
            });
            _pending.Add(task);
            _ = task.ContinueWith(completed =>
            {
                lock (_lifecycleLock)
                {
                    _pending.Remove(completed);
                }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_shutdownTask is not null)
            {
                throw new InvalidOperationException("The Last.fm hosted service cannot be restarted after stopping.");
            }
            if (!_started)
            {
                _sessionManager.PlaybackStart += PlaybackStart;
                _sessionManager.PlaybackProgress += PlaybackProgress;
                _sessionManager.PlaybackStopped += PlaybackStopped;
                _userDataManager.UserDataSaved += UserDataSaved;
                _started = true;
            }
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task shutdown;
        lock (_lifecycleLock)
        {
            shutdown = BeginShutdown();
        }
        await shutdown.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task BeginShutdown()
    {
        Unsubscribe();
        return _shutdownTask ??= DrainAsync(_pending.ToArray());
    }

    private async Task DrainAsync(Task[] pending)
    {
        try
        {
            try
            {
                await _lifetime.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError("Last.fm shutdown cancellation failed ({FailureType})", exception.GetType().Name);
            }
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
        finally
        {
            _playbackTracker.Clear();
            _apiClient.Dispose();
            _concurrency.Dispose();
            _lifetime.Dispose();
        }
    }

    private void Unsubscribe()
    {
        if (_started)
        {
            _sessionManager.PlaybackStart -= PlaybackStart;
            _sessionManager.PlaybackProgress -= PlaybackProgress;
            _sessionManager.PlaybackStopped -= PlaybackStopped;
            _userDataManager.UserDataSaved -= UserDataSaved;
            _started = false;
        }
    }

    public void Dispose()
    {
        lock (_lifecycleLock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _ = BeginShutdown();
            if (ReferenceEquals(Instance, this))
            {
                Instance = null;
            }
        }
        GC.SuppressFinalize(this);
    }
}
