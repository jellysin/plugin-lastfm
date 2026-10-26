using System.Runtime.CompilerServices;
using JellySin.Plugin.Lastfm.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Tasks;

public sealed class TaskTriggerRearmTests
{
    private readonly Mock<ITaskManager> _taskManager = new();
    private readonly List<IScheduledTaskWorker> _workers = [];

    public TaskTriggerRearmTests() => _taskManager.SetupGet(manager => manager.ScheduledTasks).Returns(_workers);

    [Fact]
    public async Task FirstRunOfAPluginTask_ReArmsItsTriggers()
    {
        var ct = TestContext.Current.CancellationToken;
        var delivery = DeliveryWorker();
        _workers.Add(delivery.Object);
        await StartRearmAsync(ct);
        delivery.Verify(worker => worker.ReloadTriggerEvents(), Times.Never);

        Complete(delivery.Object);

        delivery.Verify(worker => worker.ReloadTriggerEvents(), Times.Once);
    }

    [Fact]
    public async Task LaterRuns_DoNotReArmAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var delivery = DeliveryWorker();
        await StartRearmAsync(ct);

        Complete(delivery.Object, times: 3);

        delivery.Verify(worker => worker.ReloadTriggerEvents(), Times.Once);
    }

    [Fact]
    public async Task ARunThatFinishedBeforeStart_IsReArmedAtStart()
    {
        var ct = TestContext.Current.CancellationToken;
        var finished = Worker(PluginTask(), new TaskResult { Status = TaskCompletionStatus.Completed });
        _workers.Add(finished.Object);
        await StartRearmAsync(ct);

        Complete(finished.Object);

        finished.Verify(worker => worker.ReloadTriggerEvents(), Times.Once);
    }

    [Fact]
    public async Task TasksOfOtherAssemblies_AreLeftAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var foreignTask = new Mock<IScheduledTask>();
        foreignTask.SetupGet(task => task.Key).Returns("ForeignTask");
        var foreign = Worker(foreignTask.Object, new TaskResult { Status = TaskCompletionStatus.Completed });
        _workers.Add(foreign.Object);
        await StartRearmAsync(ct);

        Complete(foreign.Object);

        foreign.Verify(worker => worker.ReloadTriggerEvents(), Times.Never);
    }

    [Fact]
    public async Task AfterStop_CompletionsAreIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        var delivery = DeliveryWorker();
        var rearm = await StartRearmAsync(ct);
        await rearm.StopAsync(ct);

        Complete(delivery.Object);

        delivery.Verify(worker => worker.ReloadTriggerEvents(), Times.Never);
    }

    [Fact]
    public async Task AFailedReArm_IsRetriedOnTheNextRun()
    {
        var ct = TestContext.Current.CancellationToken;
        var delivery = DeliveryWorker();
        delivery.SetupSequence(worker => worker.ReloadTriggerEvents()).Throws(new ObjectDisposedException("trigger")).Pass();
        await StartRearmAsync(ct);

        Complete(delivery.Object, times: 3);

        delivery.Verify(worker => worker.ReloadTriggerEvents(), Times.Exactly(2));
    }

    private static Mock<IScheduledTaskWorker> DeliveryWorker() => Worker(PluginTask(), result: null);

    private async Task<TaskTriggerRearm> StartRearmAsync(CancellationToken ct)
    {
        var rearm = new TaskTriggerRearm(_taskManager.Object, NullLogger<TaskTriggerRearm>.Instance);
        await rearm.StartAsync(ct);
        return rearm;
    }

    // The service recognises plugin tasks by their assembly; the task itself is never executed here.
    private static ScrobbleDeliveryTask PluginTask() => (ScrobbleDeliveryTask)RuntimeHelpers.GetUninitializedObject(typeof(ScrobbleDeliveryTask));

    private static Mock<IScheduledTaskWorker> Worker(IScheduledTask task, TaskResult? result)
    {
        var worker = new Mock<IScheduledTaskWorker>();
        worker.SetupGet(value => value.ScheduledTask).Returns(task);
        // The interface declares the result non-null, yet a worker whose task never ran returns null.
        worker.SetupGet(value => value.LastExecutionResult).Returns(result!);
        return worker;
    }

    private void Complete(IScheduledTaskWorker worker, int times = 1)
    {
        for (var i = 0; i < times; i++)
        {
            _taskManager.Raise(
                manager => manager.TaskCompleted += null,
                new TaskCompletionEventArgs(worker, new TaskResult { Status = TaskCompletionStatus.Completed }));
        }
    }
}
