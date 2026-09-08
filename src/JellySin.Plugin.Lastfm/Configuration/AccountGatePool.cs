namespace JellySin.Plugin.Lastfm.Configuration;

/// <summary>Per-user mutation leases, reclaimed as soon as their final holder/waiter leaves.</summary>
internal sealed class AccountGatePool : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private readonly CancellationTokenSource _stopping = new();
    private bool _disposed;

    public async Task<IDisposable> AcquireAsync(Guid userId, CancellationToken cancellationToken, bool cleanup = false)
    {
        Entry entry;
        CancellationToken stopping;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_entries.TryGetValue(userId, out entry!))
            {
                if (_entries.Count >= 256) throw new InvalidOperationException("Too many concurrent account changes.");
                entry = new Entry(userId);
                _entries.Add(userId, entry);
            }
            // Reserve bounded waiters for disconnect after it cancels ordinary mutations.
            if (entry.Users >= (cleanup ? 16 : 8)) throw new InvalidOperationException("Too many concurrent account changes for this user.");
            entry.Users++;
            stopping = _stopping.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping);
        try { await entry.Gate.WaitAsync(linked.Token).ConfigureAwait(false); }
        catch { Release(entry, false); throw; }
        return new Lease(this, entry);
    }

    private void Release(Entry entry, bool held)
    {
        lock (_sync)
        {
            if (held) entry.Gate.Release();
            if (--entry.Users != 0) return;
            _entries.Remove(entry.UserId);
            entry.Gate.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; }
        _stopping.Cancel();
        _stopping.Dispose();
    }

    private sealed class Entry(Guid userId)
    {
        public Guid UserId { get; } = userId;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private sealed class Lease(AccountGatePool owner, Entry entry) : IDisposable
    {
        private AccountGatePool? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(entry, true);
    }
}
