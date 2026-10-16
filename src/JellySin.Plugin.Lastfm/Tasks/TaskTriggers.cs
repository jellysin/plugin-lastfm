using MediaBrowser.Model.Tasks;

namespace JellySin.Plugin.Lastfm.Tasks;

/// <summary>The default triggers of the plugin's tasks.</summary>
internal static class TaskTriggers
{
    /// <summary>
    /// Runs shortly after the server starts, then every <paramref name="interval"/>. Jellyfin's interval trigger alone
    /// waits an hour before a task's first run and at least a minute plus the interval after a restart, so work that a
    /// restart interrupted would otherwise wait that long.
    /// </summary>
    public static TaskTriggerInfo[] AtStartupAndEvery(TimeSpan interval) =>
    [
        new() { Type = TaskTriggerInfoType.StartupTrigger },
        new() { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = interval.Ticks },
    ];
}
