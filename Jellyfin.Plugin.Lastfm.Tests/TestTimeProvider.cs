namespace Jellyfin.Plugin.Lastfm.Tests;

internal sealed class TestTimeProvider : TimeProvider
{
    private long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero).AddTicks(_ticks);
    public void Advance(int seconds) => _ticks += TimeSpan.FromSeconds(seconds).Ticks;
}
