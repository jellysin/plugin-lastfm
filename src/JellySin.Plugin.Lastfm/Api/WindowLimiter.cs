namespace JellySin.Plugin.Lastfm.Api;

/// <summary>A fixed-memory bound on how often something happens within one window of time.</summary>
public abstract class WindowLimiter(TimeProvider clock, int limit, TimeSpan window)
{
    private readonly object _gate = new();
    private long _window = clock.GetTimestamp();
    private int _requests;

    public bool TryAcquire()
    {
        lock (_gate)
        {
            var now = clock.GetTimestamp();
            if (clock.GetElapsedTime(_window, now) >= window)
            {
                _window = now;
                _requests = 0;
            }

            if (_requests >= limit) return false;
            _requests++;
            return true;
        }
    }
}

/// <summary>
/// A bound on the log lines for returns from Last.fm that name no user. The return address answers without a sign-in,
/// so a stranger could otherwise fill the server log one request at a time.
/// </summary>
public sealed class ReturnLogLimiter(TimeProvider clock) : WindowLimiter(clock, 20, TimeSpan.FromMinutes(1));
