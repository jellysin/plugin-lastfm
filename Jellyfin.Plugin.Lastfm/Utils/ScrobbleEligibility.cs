using System;

namespace Jellyfin.Plugin.Lastfm.Utils;

public static class ScrobbleEligibility
{
    public static bool IsEligible(long? durationTicks, long? playedTicks)
    {
        if (durationTicks is not > 30 * TimeSpan.TicksPerSecond || playedTicks is not >= 0)
        {
            return false;
        }
        return playedTicks >= 4 * TimeSpan.TicksPerMinute || playedTicks >= durationTicks / 2.0;
    }
}
