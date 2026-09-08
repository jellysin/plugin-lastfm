using JellySin.Plugin.Lastfm.Features;

namespace JellySin.Plugin.Lastfm.Tests.Features;

public sealed class FeatureLockTests
{
    [Fact]
    public async Task UnrelatedUsersWithTheSameStripeHashDoNotBlockOneAnother()
    {
        using var gates = new FeatureLocks();
        var first = new Guid(1, 0, 0, new byte[8]);
        var second = new Guid(65, 0, 0, new byte[8]);
        Assert.Equal((uint)first.GetHashCode() % 64, (uint)second.GetHashCode() % 64);
        using var held = await gates.EnterAsync(first, TestContext.Current.CancellationToken);
        using var independent = await gates.EnterAsync(second, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CancelledWaiterReleasesItsReferenceWithoutUnlockingTheHolder()
    {
        using var gates = new FeatureLocks();
        var user = Guid.NewGuid();
        var held = await gates.EnterAsync(user, TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        var waiting = gates.EnterAsync(user, cancelled.Token);
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        var next = gates.EnterAsync(user, TestContext.Current.CancellationToken);
        Assert.False(next.IsCompleted);
        held.Dispose();
        using var acquired = await next.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
    }
}
