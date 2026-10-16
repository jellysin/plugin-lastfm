using System.Collections.Concurrent;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Tasks;

/// <summary>
/// Re-arms the triggers of the plugin's tasks once each task has run in this process. Jellyfin arms a task's interval
/// trigger an hour out while the task has never run, and after the startup run it restarts only the startup trigger, so
/// on a fresh server the interval otherwise starts an hour late and scrobbles refused in that hour wait for it.
/// </summary>
public sealed class TaskTriggerRearm(ITaskManager taskManager, ILogger<TaskTriggerRearm> logger) : IHostedService
{
    // One entry per plugin task type, so the set is bounded by the tasks this assembly declares.
    private readonly ConcurrentDictionary<string, byte> _rearmed = new(StringComparer.Ordinal);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        taskManager.TaskCompleted += OnTaskCompleted;
        // A startup run that finished before this service subscribed has a result already; its interval waits too.
        foreach (var worker in taskManager.ScheduledTasks.Where(worker => Owned(worker) && worker.LastExecutionResult is not null))
        {
            Rearm(worker);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        taskManager.TaskCompleted -= OnTaskCompleted;
        return Task.CompletedTask;
    }

    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
    {
        if (Owned(e.Task)) Rearm(e.Task);
    }

    private static bool Owned(IScheduledTaskWorker worker) => worker.ScheduledTask.GetType().Assembly == typeof(TaskTriggerRearm).Assembly;

    private void Rearm(IScheduledTaskWorker worker)
    {
        if (!_rearmed.TryAdd(worker.ScheduledTask.Key, 0)) return;
        try
        {
            worker.ReloadTriggerEvents();
        }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
        {
            _rearmed.TryRemove(worker.ScheduledTask.Key, out _);
            logger.LogWarning("Re-arming the triggers of task {TaskKey} failed ({ErrorType}).", worker.ScheduledTask.Key, error.GetType().Name);
        }
    }
}
