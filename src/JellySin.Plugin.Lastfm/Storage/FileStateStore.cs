using System.Text.Json;

namespace JellySin.Plugin.Lastfm.Storage;

/// <summary>Single-process, atomic document storage. All Last.fm persistence shares one quota.</summary>
public sealed class FileStateStore : IStateStore, IDisposable
{
    public const long DefaultBudget = 80_000_000;
    private const int MaxDocuments = 8192;
    private const int ReservedDocuments = 1024;
    private const int MaxDocumentBytes = 8_000_000;
    private readonly string _root;
    private readonly long _budget;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, StoredFile> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, long>> _native = new(StringComparer.Ordinal);
    private long _storedBytes;
    private long _nativeBytes;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public FileStateStore(string root, long budget = DefaultBudget)
    {
        _root = Path.GetFullPath(root);
        _budget = budget;
        Directory.CreateDirectory(_root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        InitializeInventory();
    }

    public async Task<T?> ReadAsync<T>(Guid userId, string key, CancellationToken cancellationToken)
    {
        var path = GetPath(userId, key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return default;
            if (new FileInfo(path).Length > MaxDocumentBytes) throw new StorageBudgetException();
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task WriteAsync<T>(Guid userId, string key, T value, CancellationToken cancellationToken)
    {
        var path = GetPath(userId, key);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length > MaxDocumentBytes) throw new StorageBudgetException();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await WriteLockedAsync(path, bytes, cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task<T> UpdateAsync<T>(Guid userId, string key, Func<T?, T> update, CancellationToken cancellationToken)
    {
        var path = GetPath(userId, key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            T? current = default;
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > MaxDocumentBytes) throw new StorageBudgetException();
                current = JsonSerializer.Deserialize<T>(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false), JsonOptions);
            }
            var next = update(current);
            await WriteLockedAsync(path, JsonSerializer.SerializeToUtf8Bytes(next, JsonOptions), cancellationToken).ConfigureAwait(false);
            return next;
        }
        finally { _gate.Release(); }
    }

    private async Task WriteLockedAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var disconnected = Path.Combine(Path.GetDirectoryName(path)!, "disconnected");
        if (File.Exists(disconnected) && Path.GetFileName(path) is not ("account.json" or "attempt.json"))
            throw new AccountDisconnectedException();
        if (bytes.Length > MaxDocumentBytes) throw new StorageBudgetException();
        var oldSize = _files.GetValueOrDefault(path)?.Bytes ?? 0;
        var isDurable = Path.GetFileName(path) is "account.json" or "outbox.json" or "attempt.json" or "credentials.json";
        EnsureCapacity(path, bytes.Length, oldSize, isDurable, cancellationToken);
        if (!Directory.Exists(Path.GetDirectoryName(path)) && Directory.EnumerateDirectories(_root).Take(MaxDocuments).Count() >= MaxDocuments)
            throw new StorageBudgetException();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".pending";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
            _storedBytes += bytes.Length - oldSize;
            _files[path] = new(bytes.Length, DateTime.UtcNow);
            if (Path.GetFileName(path) == "account.json") File.Delete(disconnected);
        }
        finally { File.Delete(temporary); }
    }

    public async Task DeleteAsync(Guid userId, string key, CancellationToken cancellationToken)
    {
        var path = GetPath(userId, key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { RemoveFile(path); }
        finally { _gate.Release(); }
    }

    public async Task DeleteUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty) throw new ArgumentException("A user identity is required.", nameof(userId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(_root, userId.ToString("N"));
            if (Directory.Exists(path))
            {
                foreach (var file in _files.Keys.Where(file => Path.GetDirectoryName(file) == path).ToArray()) RemoveFile(file);
                Directory.Delete(path, true);
            }
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "disconnected"), string.Empty);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<Guid>> GetUsersAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directories = Directory.EnumerateDirectories(_root).Take(MaxDocuments + 1).ToArray();
            if (directories.Length > MaxDocuments) throw new StorageBudgetException();
            return directories
                .Select(Path.GetFileName).Select(name => Guid.TryParseExact(name, "N", out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty).ToArray();
        }
        finally { _gate.Release(); }
    }

    private string GetPath(Guid userId, string key)
    {
        if (key.Length is < 1 or > 100 || key.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '-')))
            throw new ArgumentException("Invalid state key.", nameof(key));
        return Path.Combine(_root, userId.ToString("N"), key + ".json");
    }

    public void Dispose() => _gate.Dispose();

    public async Task ReserveNativeAsync(string identity, long bytes, CancellationToken cancellationToken)
    {
        if (identity.Length != 64 || identity.Any(c => !char.IsAsciiHexDigit(c)) || bytes < 1024) throw new ArgumentException("Invalid native storage reservation.", nameof(identity));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var key = "native-reservations-" + identity[..2];
            if (!_native.TryGetValue(key, out var entries)) _native[key] = entries = [];
            var previous = entries.GetValueOrDefault(identity);
            if (bytes <= previous) return;
            if (previous == 0 && entries.Count >= 4096) throw new StorageBudgetException();
            entries[identity] = bytes;
            _nativeBytes += bytes - previous;
            try { await WriteLockedAsync(GetPath(Guid.Empty, key), JsonSerializer.SerializeToUtf8Bytes(entries, JsonOptions), cancellationToken).ConfigureAwait(false); }
            catch
            {
                _nativeBytes -= bytes - previous;
                if (previous == 0) entries.Remove(identity); else entries[identity] = previous;
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    private void InitializeInventory()
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var pending = Directory.EnumerateFiles(_root, "*.json.pending", options).Take(MaxDocuments + 1).ToArray();
        if (pending.Length > MaxDocuments) throw new StorageBudgetException();
        foreach (var orphan in pending) File.Delete(orphan);
        foreach (var file in Directory.EnumerateFiles(_root, "*.json", options).Take(MaxDocuments + 1))
        {
            if (_files.Count >= MaxDocuments) throw new StorageBudgetException();
            var info = new FileInfo(file);
            if (info.Length > MaxDocumentBytes) throw new StorageBudgetException();
            _files[file] = new(info.Length, info.LastWriteTimeUtc);
            _storedBytes += info.Length;
            if (!Path.GetFileName(file).StartsWith("native-reservations-", StringComparison.Ordinal)) continue;
            var entries = JsonSerializer.Deserialize<Dictionary<string, long>>(File.ReadAllBytes(file), JsonOptions) ?? [];
            if (entries.Count > 4096 || entries.Any(entry => entry.Key.Length != 64 || entry.Value < 1024 || entry.Value > _budget)) throw new StorageBudgetException();
            _native[Path.GetFileNameWithoutExtension(file)] = entries;
            _nativeBytes += entries.Values.Sum();
        }
        if (_storedBytes + _nativeBytes > _budget) throw new StorageBudgetException();
    }

    private void EnsureCapacity(string path, long size, long oldSize, bool durable, CancellationToken ct)
    {
        var stagingReserve = Math.Min(MaxDocumentBytes, _budget / 10);
        var allowance = durable ? _budget - stagingReserve : _budget * 3 / 4;
        var documents = durable ? MaxDocuments : MaxDocuments - ReservedDocuments;
        bool Fits() => _storedBytes + _nativeBytes - oldSize + size <= allowance
            && _storedBytes + _nativeBytes + size <= _budget
            && _files.Count + (oldSize == 0 ? 1 : 0) <= documents;
        if (Fits()) return;
        foreach (var cached in _files.Where(entry => entry.Key != path && Path.GetFileName(entry.Key).StartsWith("cache-", StringComparison.Ordinal))
                     .OrderBy(entry => entry.Value.Written).Select(entry => entry.Key).ToArray())
        {
            ct.ThrowIfCancellationRequested();
            RemoveFile(cached);
            if (Fits()) return;
        }
        throw new StorageBudgetException();
    }

    private void RemoveFile(string path)
    {
        File.Delete(path);
        if (_files.Remove(path, out var entry)) _storedBytes -= entry.Bytes;
    }

    private sealed record StoredFile(long Bytes, DateTime Written);
}
