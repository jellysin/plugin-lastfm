using Jellyfin.Plugin.Lastfm.Utils;
using Shouldly;

namespace Jellyfin.Plugin.Lastfm.Tests;

public sealed class ScrobbleEligibilityTests
{
    [Theory]
    [InlineData(null, 240, false)]
    [InlineData(600, null, false)]
    [InlineData(0, 240, false)]
    [InlineData(-1, 240, false)]
    [InlineData(600, -1, false)]
    [InlineData(29, 29, false)]
    [InlineData(30, 30, false)]
    [InlineData(31, 16, true)]
    [InlineData(120, 59, false)]
    [InlineData(120, 60, true)]
    [InlineData(600, 239, false)]
    [InlineData(600, 240, true)]
    public void UsesLastfmDurationAndPlaybackBoundaries(int? durationSeconds, int? playedSeconds, bool expected)
    {
        ScrobbleEligibility.IsEligible(durationSeconds * TimeSpan.TicksPerSecond, playedSeconds * TimeSpan.TicksPerSecond)
            .ShouldBe(expected);
    }

    [Fact]
    public void HalfDurationUsesTicksWithoutRoundingDown()
    {
        const long duration = 31 * TimeSpan.TicksPerSecond + 1;
        ScrobbleEligibility.IsEligible(duration, duration / 2).ShouldBeFalse();
        ScrobbleEligibility.IsEligible(duration, duration / 2 + 1).ShouldBeTrue();
    }
}
