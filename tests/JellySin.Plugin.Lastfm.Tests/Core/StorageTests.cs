using JellySin.Plugin.Lastfm.Storage;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class StorageTests
{
    [Fact]
    public async Task ConcurrentUpdatesPreserveEveryIncrement()
    {
        using var fixture = new CoreFixture();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => fixture.Store.UpdateAsync<Counter>(fixture.UserId, "counter", state => new Counter((state?.Value ?? 0) + 1), TestContext.Current.CancellationToken)));
        Assert.Equal(100, (await fixture.Store.ReadAsync<Counter>(fixture.UserId, "counter", TestContext.Current.CancellationToken))!.Value);
    }

    [Fact]
    public async Task AtomicUpdateFailurePreservesPreviousDocument()
    {
        using var fixture = new CoreFixture();
        await fixture.Store.WriteAsync(fixture.UserId, "counter", new Counter(7), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Store.UpdateAsync<Counter>(fixture.UserId, "counter", _ => throw new InvalidOperationException(), TestContext.Current.CancellationToken));
        Assert.Equal(7, (await fixture.Store.ReadAsync<Counter>(fixture.UserId, "counter", TestContext.Current.CancellationToken))!.Value);
    }

    [Fact]
    public async Task UsersCannotReadAnotherUsersDocument()
    {
        using var fixture = new CoreFixture();
        await fixture.Store.WriteAsync(fixture.UserId, "private", "secret", TestContext.Current.CancellationToken);
        Assert.Null(await fixture.Store.ReadAsync<string>(Guid.NewGuid(), "private", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("../account")]
    [InlineData("C:\\account")]
    [InlineData("")]
    [InlineData("a/b")]
    public async Task RejectsPathTraversal(string key)
    {
        using var fixture = new CoreFixture();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Store.WriteAsync(fixture.UserId, key, "data", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CacheCannotConsumeDurableReserve()
    {
        using var fixture = new CoreFixture();
        using var constrained = new FileStateStore(fixture.DirectoryPath, 1000);
        await Assert.ThrowsAsync<StorageBudgetException>(() => constrained.WriteAsync(fixture.UserId, "cache", new string('x', 900), TestContext.Current.CancellationToken));
        await constrained.WriteAsync(fixture.UserId, "outbox", new string('x', 900), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<StorageBudgetException>(() => constrained.WriteAsync(fixture.UserId, "account", new string('x', 100), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DisconnectRejectsLateBackgroundWritesAcrossRestart()
    {
        using var fixture = new CoreFixture();
        await fixture.Store.DeleteUserAsync(fixture.UserId, TestContext.Current.CancellationToken);
        using var restarted = new FileStateStore(fixture.DirectoryPath);
        await Assert.ThrowsAsync<AccountDisconnectedException>(() => restarted.WriteAsync(fixture.UserId, "feature-history", "private", TestContext.Current.CancellationToken));
        await restarted.WriteAsync(fixture.UserId, "account", "new connection", TestContext.Current.CancellationToken);
        await restarted.WriteAsync(fixture.UserId, "feature-history", "new data", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CancelledWriteDoesNotReplaceExistingData()
    {
        using var fixture = new CoreFixture();
        await fixture.Store.WriteAsync(fixture.UserId, "data", "before", TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Store.WriteAsync(fixture.UserId, "data", "after", cancellation.Token));
        Assert.Equal("before", await fixture.Store.ReadAsync<string>(fixture.UserId, "data", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CacheEvictionAllowsDurableWritesWithoutDeletingAccountState()
    {
        using var fixture = new CoreFixture();
        using var constrained = new FileStateStore(fixture.DirectoryPath, 1000);
        await constrained.WriteAsync(fixture.UserId, "account", "protected-account", TestContext.Current.CancellationToken);
        await constrained.WriteAsync(Guid.Empty, "cache-old", new string('x', 800), TestContext.Current.CancellationToken);
        await constrained.WriteAsync(fixture.UserId, "outbox", new string('x', 800), TestContext.Current.CancellationToken);
        Assert.Null(await constrained.ReadAsync<string>(Guid.Empty, "cache-old", TestContext.Current.CancellationToken));
        Assert.Equal("protected-account", await constrained.ReadAsync<string>(fixture.UserId, "account", TestContext.Current.CancellationToken));
    }

    public sealed record Counter(int Value);
}
