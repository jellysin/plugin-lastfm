namespace JellySin.Plugin.Lastfm.Api;

/// <summary>A fixed-memory global bound for the anonymous Quick Connect status adapter.</summary>
public sealed class QuickConnectLimiter(TimeProvider clock)
{
    private readonly object _gate = new();
    private long _window = clock.GetTimestamp();
    private int _requests;

    public bool TryAcquire()
    {
        lock (_gate)
        {
            var now = clock.GetTimestamp();
            if (clock.GetElapsedTime(_window, now) >= TimeSpan.FromSeconds(1))
            {
                _window = now;
                _requests = 0;
            }

            if (_requests >= 60) return false;
            _requests++;
            return true;
        }
    }
}
