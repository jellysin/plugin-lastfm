namespace JellySin.Plugin.Lastfm.Configuration;

/// <summary>Independent account-work and browser-authorization lifetimes.</summary>
internal sealed class AccountOperation : IDisposable
{
    private readonly object _sync = new();
    private CancellationTokenSource _work = new();
    private CancellationTokenSource _authorization = new();
    private Guid _revision = Guid.NewGuid();
    private bool _disposed;

    public CancellationToken Token { get { lock (_sync) return _work.Token; } }
    public bool IsCancellationRequested { get { lock (_sync) return _work.IsCancellationRequested; } }
    public AuthorizationRevision Authorization { get { lock (_sync) return new(_revision, _authorization.Token); } }

    public void Cancel()
    {
        lock (_sync) { if (!_disposed) _work.Cancel(); }
    }

    public void InvalidateAuthorization()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _revision = Guid.NewGuid();
            var previous = _authorization;
            _authorization = new();
            previous.Cancel();
            previous.Dispose();
            _work.Cancel();
        }
    }

    public void Renew(AuthorizationRevision authorization, Action publish)
    {
        lock (_sync)
        {
            authorization.Token.ThrowIfCancellationRequested();
            if (_disposed || authorization.Id != _revision) throw new OperationCanceledException(authorization.Token);
            _work.Cancel();
            _work.Dispose();
            _work = new();
            publish();
        }
    }

    public void Publish(AuthorizationRevision authorization, Action publish)
    {
        lock (_sync)
        {
            authorization.Token.ThrowIfCancellationRequested();
            if (_disposed || authorization.Id != _revision) throw new OperationCanceledException(authorization.Token);
            publish();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _work.Cancel();
            _authorization.Cancel();
            _work.Dispose();
            _authorization.Dispose();
        }
    }
}

internal sealed record AuthorizationRevision(Guid Id, CancellationToken Token);
