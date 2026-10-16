using Microsoft.Extensions.Time.Testing;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public static class TestTime
{
    /// <summary>The moment every test clock starts at.</summary>
    public static readonly DateTimeOffset Start = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    public static FakeTimeProvider Clock() => new(Start);

    extension(FakeTimeProvider clock)
    {
        public void Advance(double seconds) => clock.Advance(TimeSpan.FromSeconds(seconds));
    }
}
