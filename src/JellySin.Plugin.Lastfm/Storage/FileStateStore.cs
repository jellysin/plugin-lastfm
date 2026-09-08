using System.Text.Json;

namespace JellySin.Plugin.Lastfm.Storage;

/// <summary>Single-process, atomic document storage. All Last.fm persistence shares one quota.</summary>
public sealed class FileStateStore : IStateStore, IDisposable
{
    public const long DefaultBudget = 80_000_000;
    private const int MaxDocuments = 8192;
    private const int MaxDocumentBytes = 8_000_000;
    private readonly string _root;
    private readonly long _budget;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public FileStateStore(string root, long budget = DefaultBudget)
    {
        _root = Path.GetFullPath(root);
        _budget = budget;
        Directory.CreateDirectory(_root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
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
        var files = Directory.EnumerateFiles(_root, "*.json", SearchOption.AllDirectories).Take(MaxDocuments + 1).ToArray();
        var oldSize = File.Exists(path) ? new FileInfo(path).Length : 0;
        var isDurable = Path.GetFileName(path) is "account.json" or "outbox.json" or "attempt.json" or "credentials.json";
        var allowance = isDurable ? _budget : _budget * 7 / 8;
        var used = files.Sum(file => new FileInfo(file).Length) - oldSize + bytes.Length;
        var count = files.Length + (oldSize == 0 ? 1 : 0);
        if (used > allowance || count > MaxDocuments)
        {
            foreach (var cached in files.Where(file => file != path && Path.GetFileName(file).StartsWith("cache-", StringComparison.Ordinal)).OrderBy(File.GetLastWriteTimeUtc))
            {
                used -= new FileInfo(cached).Length;
                File.Delete(cached);
                count--;
                if (used <= allowance && count <= MaxDocuments) break;
            }
        }
        if (count > MaxDocuments || used > allowance)
            throw new StorageBudgetException();
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
            if (Path.GetFileName(path) == "account.json") File.Delete(disconnected);
        }
        finally { File.Delete(temporary); }
    }

    public async Task DeleteAsync(Guid userId, string key, CancellationToken cancellationToken)
    {
        var path = GetPath(userId, key);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { File.Delete(path); }
        finally { _gate.Release(); }
    }

    public async Task DeleteUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (userId == Guid.Empty) throw new ArgumentException("A user identity is required.", nameof(userId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(_root, userId.ToString("N"));
            if (Directory.Exists(path)) Directory.Delete(path, true);
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
}
