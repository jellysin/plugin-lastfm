using System.Collections.Concurrent;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellySin.Plugin.Lastfm.Playback;

public sealed record UserLibraryRules(
    IReadOnlyList<Guid>? ExcludedLibraries = null,
    IReadOnlyList<string>? ExcludedDevices = null,
    IReadOnlyList<string>? ExcludedMediaTypes = null);

public sealed class LibraryRuleService : IDisposable
{
    private sealed record LibraryPathPrefix(string Prefix, Guid LibraryId);

    public const int MaxExcludedDevices = 64;
    public const int MaxDeviceIdLength = 256;
    public static IReadOnlyList<string> KnownMediaTypes { get; } = ["Music", "Audiobooks", "MusicVideos"];

    private const string StoreKey = "library-rules";
    private const int MaxCachedItems = 8192;
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly IStateStore _store;
    private readonly ILibraryManager? _libraryManager;
    private readonly ILogger<LibraryRuleService> _logger;
    private readonly Func<PluginConfiguration>? _configProvider;
    private readonly ConcurrentDictionary<Guid, HashSet<Guid>> _userExcluded = new();
    private readonly ConcurrentDictionary<Guid, HashSet<string>> _userExcludedDevices = new();
    private readonly ConcurrentDictionary<Guid, HashSet<string>> _userExcludedMediaTypes = new();
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<Guid>> _itemLibraries = new();
    private readonly object _rebuildGate = new();
    private readonly object _rebuildSync = new();

    private LibraryPathPrefix[] _prefixes = [];
    private IReadOnlyDictionary<Guid, string> _virtualFolderNames = new Dictionary<Guid, string>();
    private Task _pendingRebuild = Task.CompletedTask;
    private bool _rebuildQueued;

    public LibraryRuleService(IStateStore store, ILibraryManager? libraryManager, ILogger<LibraryRuleService>? logger)
        : this(store, libraryManager, logger, null)
    {
    }

    public LibraryRuleService(IStateStore store, ILibraryManager? libraryManager = null, Func<PluginConfiguration>? configProvider = null)
        : this(store, libraryManager, null, configProvider)
    {
    }

    public LibraryRuleService(IStateStore store, ILibraryManager? libraryManager, ILogger<LibraryRuleService>? logger, Func<PluginConfiguration>? configProvider)
    {
        _store = store;
        _libraryManager = libraryManager;
        _logger = logger ?? NullLogger<LibraryRuleService>.Instance;
        _configProvider = configProvider;

        if (_libraryManager is not null)
        {
            _libraryManager.ItemAdded += OnItemAdded;
            _libraryManager.ItemRemoved += OnItemRemoved;
            _libraryManager.ItemUpdated += OnItemUpdated;
        }

        CollectionFolder.LibraryOptionsUpdated += OnLibraryOptionsUpdated;
    }

    public Task PendingRebuild => _pendingRebuild;

    public PluginConfiguration GetConfiguration() => _configProvider?.Invoke() ?? Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        RefreshVirtualFolders();
        foreach (var userId in await _store.GetUsersAsync(cancellationToken).ConfigureAwait(false))
        {
            var rules = await _store.ReadAsync<UserLibraryRules>(userId, StoreKey, cancellationToken).ConfigureAwait(false);
            if (rules is not null)
            {
                PopulateCache(userId, rules);
            }
        }
    }

    public void RefreshVirtualFolders()
    {
        try
        {
            RebuildPrefixTable();
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Failed to refresh virtual folders ({ErrorType}).", exception.GetType().Name);
        }
    }

    public IReadOnlyList<(Guid Id, string Name)> GetAvailableLibraries()
    {
        if (_libraryManager is not null)
        {
            try
            {
                return _libraryManager.GetVirtualFolders()
                    .Select(vf => (Id: Guid.TryParse(vf.ItemId, out var id) ? id : Guid.Empty, Name: vf.Name))
                    .Where(x => x.Id != Guid.Empty).ToList();
            }
            catch (Exception exception)
            {
                _logger.LogWarning("Failed to get virtual folders ({ErrorType}).", exception.GetType().Name);
            }
        }

        var cached = Volatile.Read(ref _virtualFolderNames);
        return cached.Select(kv => (Id: kv.Key, Name: kv.Value)).ToList();
    }

    public async Task<IReadOnlyList<Guid>> GetUserExcludedLibrariesAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (_userExcluded.TryGetValue(userId, out var cached)) return cached.ToArray();
        var stored = await _store.ReadAsync<UserLibraryRules>(userId, StoreKey, cancellationToken).ConfigureAwait(false);
        PopulateCache(userId, stored);
        return _userExcluded.GetValueOrDefault(userId)?.ToArray() ?? [];
    }

    public async Task<IReadOnlyList<string>> GetUserExcludedDevicesAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (_userExcludedDevices.TryGetValue(userId, out var cached)) return cached.ToArray();
        var stored = await _store.ReadAsync<UserLibraryRules>(userId, StoreKey, cancellationToken).ConfigureAwait(false);
        PopulateCache(userId, stored);
        return _userExcludedDevices.GetValueOrDefault(userId)?.ToArray() ?? [];
    }

    public async Task<IReadOnlyList<string>> GetUserExcludedMediaTypesAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (_userExcludedMediaTypes.TryGetValue(userId, out var cached)) return cached.ToArray();
        var stored = await _store.ReadAsync<UserLibraryRules>(userId, StoreKey, cancellationToken).ConfigureAwait(false);
        PopulateCache(userId, stored);
        return _userExcludedMediaTypes.GetValueOrDefault(userId)?.ToArray() ?? [];
    }

    public async Task SetUserExcludedLibrariesAsync(Guid userId, IReadOnlyList<Guid> excludedLibraries, CancellationToken cancellationToken)
    {
        var bounded = excludedLibraries.Distinct().Take(PluginConfiguration.MaxConfiguredLibraries).ToArray();
        await _store.UpdateAsync<UserLibraryRules>(userId, StoreKey, current =>
            new UserLibraryRules(
                ExcludedLibraries: bounded,
                ExcludedDevices: current?.ExcludedDevices,
                ExcludedMediaTypes: current?.ExcludedMediaTypes),
            cancellationToken).ConfigureAwait(false);
        _userExcluded[userId] = new HashSet<Guid>(bounded);
    }

    public async Task SetUserExcludedDevicesAsync(Guid userId, IReadOnlyList<string> excludedDevices, CancellationToken cancellationToken)
    {
        var bounded = excludedDevices
            .Where(d => !string.IsNullOrEmpty(d) && d.Length <= MaxDeviceIdLength)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxExcludedDevices)
            .ToArray();
        await _store.UpdateAsync<UserLibraryRules>(userId, StoreKey, current =>
            new UserLibraryRules(
                ExcludedLibraries: current?.ExcludedLibraries,
                ExcludedDevices: bounded,
                ExcludedMediaTypes: current?.ExcludedMediaTypes),
            cancellationToken).ConfigureAwait(false);
        _userExcludedDevices[userId] = new HashSet<string>(bounded, StringComparer.Ordinal);
    }

    public async Task SetUserExcludedMediaTypesAsync(Guid userId, IReadOnlyList<string> excludedMediaTypes, CancellationToken cancellationToken)
    {
        var bounded = excludedMediaTypes
            .Where(KnownMediaTypes.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await _store.UpdateAsync<UserLibraryRules>(userId, StoreKey, current =>
            new UserLibraryRules(
                ExcludedLibraries: current?.ExcludedLibraries,
                ExcludedDevices: current?.ExcludedDevices,
                ExcludedMediaTypes: bounded),
            cancellationToken).ConfigureAwait(false);
        _userExcludedMediaTypes[userId] = new HashSet<string>(bounded, StringComparer.Ordinal);
    }

    private void PopulateCache(Guid userId, UserLibraryRules? rules)
    {
        if (rules?.ExcludedLibraries is { Count: > 0 } excluded)
        {
            _userExcluded[userId] = new HashSet<Guid>(excluded.Take(PluginConfiguration.MaxConfiguredLibraries));
        }
        else
        {
            _userExcluded.TryRemove(userId, out _);
        }

        if (rules?.ExcludedDevices is { Count: > 0 } excludedDevices)
        {
            _userExcludedDevices[userId] = new HashSet<string>(
                excludedDevices.Where(d => !string.IsNullOrEmpty(d) && d.Length <= MaxDeviceIdLength).Take(MaxExcludedDevices),
                StringComparer.Ordinal);
        }
        else
        {
            _userExcludedDevices.TryRemove(userId, out _);
        }

        if (rules?.ExcludedMediaTypes is { Count: > 0 } excludedTypes)
        {
            _userExcludedMediaTypes[userId] = new HashSet<string>(
                excludedTypes.Where(KnownMediaTypes.Contains),
                StringComparer.Ordinal);
        }
        else
        {
            _userExcludedMediaTypes.TryRemove(userId, out _);
        }
    }

    public IReadOnlyList<Guid>? ResolveItemLibraries(BaseItem item)
    {
        if (_itemLibraries.TryGetValue(item.Id, out var cached)) return cached;
        var path = !string.IsNullOrEmpty(item.Path) ? item.Path : item.ContainingFolderPath;
        if (!string.IsNullOrEmpty(path))
        {
            var normalized = NormalizePath(path);
            var prefixes = Volatile.Read(ref _prefixes);
            foreach (var prefix in prefixes)
            {
                if (normalized.StartsWith(prefix.Prefix, PathComparison))
                {
                    var resolved = new[] { prefix.LibraryId };
                    if (_itemLibraries.Count < MaxCachedItems)
                    {
                        _itemLibraries.TryAdd(item.Id, resolved);
                    }

                    return resolved;
                }
            }
        }

        return null;
    }

    public static string? MediaTypeOf(BaseItem item) => item switch
    {
        AudioBook => "Audiobooks",
        Audio => "Music",
        MusicVideo => "MusicVideos",
        _ => null
    };

    public static bool IsAdminAllowed(PluginConfiguration config, string mediaType) => mediaType switch
    {
        "Audiobooks" => config.ScrobbleAudiobooks,
        "Music" => config.ScrobbleMusic,
        "MusicVideos" => config.ScrobbleMusicVideos,
        _ => false
    };

    public bool IsMediaTypeAllowed(BaseItem item) => MediaTypeOf(item) is { } mediaType && IsAdminAllowed(GetConfiguration(), mediaType);

    public bool IsScrobblingAllowed(Guid userId, BaseItem item, string? deviceId = null)
    {
        if (deviceId is not null)
        {
            var userExcludedDevices = _userExcludedDevices.GetValueOrDefault(userId);
            if (userExcludedDevices is not null && userExcludedDevices.Contains(deviceId)) return false;
        }

        if (MediaTypeOf(item) is { } mediaType)
        {
            var userExcludedTypes = _userExcludedMediaTypes.GetValueOrDefault(userId);
            if (userExcludedTypes is not null && userExcludedTypes.Contains(mediaType)) return false;
        }

        var config = GetConfiguration();
        var mode = config.LibraryMode;
        var adminLibs = config.LibraryIds;
        var itemLibs = ResolveItemLibraries(item);

        if (itemLibs is null || itemLibs.Count == 0)
            return mode != LibraryFilterMode.AllowList;

        var userExcluded = _userExcluded.GetValueOrDefault(userId);
        if (userExcluded is not null && itemLibs.Any(userExcluded.Contains)) return false;

        return mode switch
        {
            LibraryFilterMode.AllowList => adminLibs.Length > 0 && itemLibs.Any(adminLibs.Contains),
            LibraryFilterMode.BlockList => adminLibs.Length == 0 || !itemLibs.Any(adminLibs.Contains),
            _ => true
        };
    }

    // A library scan raises many folder events; they coalesce into the running rebuild plus at most one queued after it.
    public void ScheduleRebuild()
    {
        lock (_rebuildGate)
        {
            if (_rebuildQueued) return;
            _rebuildQueued = true;
            _pendingRebuild = _pendingRebuild.ContinueWith(_ =>
            {
                lock (_rebuildGate) _rebuildQueued = false;
                try
                {
                    RebuildPrefixTable();
                }
                catch (Exception exception)
                {
                    _logger.LogWarning("Failed to refresh virtual folders ({ErrorType}).", exception.GetType().Name);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    public void Dispose()
    {
        if (_libraryManager is not null)
        {
            _libraryManager.ItemAdded -= OnItemAdded;
            _libraryManager.ItemRemoved -= OnItemRemoved;
            _libraryManager.ItemUpdated -= OnItemUpdated;
        }

        CollectionFolder.LibraryOptionsUpdated -= OnLibraryOptionsUpdated;
    }

    private void RebuildPrefixTable()
    {
        if (_libraryManager is null) return;
        lock (_rebuildSync)
        {
            var list = new List<LibraryPathPrefix>();
            var folderNames = new Dictionary<Guid, string>();
            foreach (var vf in _libraryManager.GetVirtualFolders())
            {
                if (!Guid.TryParse(vf.ItemId, out var id)) continue;
                folderNames[id] = vf.Name;
                foreach (var loc in vf.Locations ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(loc))
                    {
                        list.Add(new LibraryPathPrefix(NormalizeLocation(loc), id));
                    }
                }
            }

            var sorted = list.OrderByDescending(p => p.Prefix.Length).ToArray();
            Volatile.Write(ref _prefixes, sorted);
            Volatile.Write(ref _virtualFolderNames, folderNames);
            _itemLibraries.Clear();
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs args)
    {
        if (args.Item is null or CollectionFolder or ICollectionFolder)
        {
            ScheduleRebuild();
        }
    }

    private void OnItemRemoved(object? sender, ItemChangeEventArgs args)
    {
        if (args.Item is not null)
        {
            _itemLibraries.TryRemove(args.Item.Id, out _);
        }

        if (args.Item is null or CollectionFolder or ICollectionFolder)
        {
            ScheduleRebuild();
        }
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs args)
    {
        if (args.Item is null or CollectionFolder or ICollectionFolder)
        {
            ScheduleRebuild();
        }
    }

    private void OnLibraryOptionsUpdated(object? sender, LibraryOptionsUpdatedEventArgs args)
    {
        ScheduleRebuild();
    }

    private static string NormalizeLocation(string path) =>
        path.Trim().Replace('\\', '/').TrimEnd('/') + '/';

    private static string NormalizePath(string path) =>
        path.Trim().Replace('\\', '/');
}
