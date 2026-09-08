using JellySin.Plugin.Lastfm.Storage;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class StorageTests
{
    [Fact]
    public async Task RestartReclaimsCrashLeftTemporaryBeforeAcceptingNewWrites()
    {
        using var fixture = new CoreFixture();
        var directory = Path.Combine(fixture.DirectoryPath, fixture.UserId.ToString("N"));
        Directory.CreateDirectory(directory);
        var pending = Path.Combine(directory, "outbox.json.pending");
        await File.WriteAllTextAsync(pending, new string('x', 2000), TestContext.Current.CancellationToken);
        using var restarted = new FileStateStore(fixture.DirectoryPath, 1000);
        Assert.False(File.Exists(pending));
        await restarted.WriteAsync(fixture.UserId, "outbox", new string('x', 800), TestContext.Current.CancellationToken);
        Assert.True(Directory.EnumerateFiles(directory).Sum(file => new FileInfo(file).Length) <= 1000);
    }

    [Fact]
    public async Task ReplacementMustFitTheOldAndStagingDocumentsTogether()
    {
        using var fixture = new CoreFixture();
        using var constrained = new FileStateStore(fixture.DirectoryPath, 1000);
        await constrained.WriteAsync(fixture.UserId, "outbox", new string('x', 600), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<StorageBudgetException>(() => constrained.WriteAsync(fixture.UserId, "outbox", new string('x', 600), TestContext.Current.CancellationToken));
        Assert.Equal(new string('x', 600), await constrained.ReadAsync<string>(fixture.UserId, "outbox", TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(fixture.DirectoryPath, "*.pending", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task NativeReservationsAreCompactPersistentAndLeaveDurableDocumentCapacity()
    {
        using var fixture = new CoreFixture();
        var shards = new Dictionary<string, Dictionary<string, long>>();
        for (var index = 0; index < 9000; index++)
        {
            var identity = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(index)));
            if (!shards.TryGetValue(identity[..2], out var entries)) shards[identity[..2]] = entries = [];
            entries[identity] = 1024;
        }
        var directory = Path.Combine(fixture.DirectoryPath, Guid.Empty.ToString("N"));
        Directory.CreateDirectory(directory);
        foreach (var shard in shards)
            await File.WriteAllTextAsync(Path.Combine(directory, "native-reservations-" + shard.Key + ".json"),
                System.Text.Json.JsonSerializer.Serialize(shard.Value), TestContext.Current.CancellationToken);
        var files = Directory.EnumerateFiles(fixture.DirectoryPath, "*.json", SearchOption.AllDirectories).ToArray();
        Assert.InRange(files.Length, 1, 256);
        Assert.True(files.Sum(path => new FileInfo(path).Length) < 1_000_000);
        using var restarted = new FileStateStore(fixture.DirectoryPath);
        await restarted.ReserveNativeAsync(new string('e', 64), 1024, TestContext.Current.CancellationToken);
        await restarted.WriteAsync(fixture.UserId, "outbox", "durable", TestContext.Current.CancellationToken);
        Assert.Equal("durable", await restarted.ReadAsync<string>(fixture.UserId, "outbox", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NativeCopyBytesCountAcrossRestartAndReservationFailureRollsBack()
    {
        using var fixture = new CoreFixture();
        var identity = new string('a', 64);
        using (var constrained = new FileStateStore(fixture.DirectoryPath, 10000))
        {
            await constrained.ReserveNativeAsync(identity, 6000, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<StorageBudgetException>(() => constrained.ReserveNativeAsync(identity, 8000, TestContext.Current.CancellationToken));
            await constrained.WriteAsync(fixture.UserId, "account", new string('x', 1000), TestContext.Current.CancellationToken);
        }
        using var restarted = new FileStateStore(fixture.DirectoryPath, 10000);
        await Assert.ThrowsAsync<StorageBudgetException>(() => restarted.WriteAsync(fixture.UserId, "outbox", new string('x', 3000), TestContext.Current.CancellationToken));
        await restarted.WriteAsync(fixture.UserId, "outbox", new string('x', 1000), TestContext.Current.CancellationToken);
    }
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
        await constrained.WriteAsync(fixture.UserId, "outbox", new string('x', 890), TestContext.Current.CancellationToken);
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
        await constrained.WriteAsync(Guid.Empty, "cache-old", new string('x', 700), TestContext.Current.CancellationToken);
        await constrained.WriteAsync(fixture.UserId, "outbox", new string('x', 800), TestContext.Current.CancellationToken);
        Assert.Null(await constrained.ReadAsync<string>(Guid.Empty, "cache-old", TestContext.Current.CancellationToken));
        Assert.Equal("protected-account", await constrained.ReadAsync<string>(fixture.UserId, "account", TestContext.Current.CancellationToken));
    }

    public sealed record Counter(int Value);
}
