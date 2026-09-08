using System.Text.Json;
using JellySin.Plugin.Lastfm.Configuration;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class AccountConcurrencyTests
{
    [Fact]
    public async Task DisconnectImmediatelyCancelsPendingGrantAndCannotBeUndoneByItsResponse()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var attempt = await fixture.Accounts.BeginAsync(fixture.UserId, TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.AsyncHandler = async (_, _, token) =>
        {
            entered.SetResult(token);
            // Deliberately emulate a transport which delivers its response after cancellation.
            await release.Task;
            return JsonDocument.Parse("{\"session\":{\"name\":\"StaleListener\",\"key\":\"stale-key\"}}");
        };
        var background = fixture.Accounts.GetOperationToken(fixture.UserId);
        var finish = fixture.Accounts.FinishAsync(fixture.UserId, attempt.AttemptId, TestContext.Current.CancellationToken);
        var authorization = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var disconnect = fixture.Accounts.DisconnectAsync(fixture.UserId, TestContext.Current.CancellationToken);
        try
        {
            Assert.True(authorization.IsCancellationRequested);
            Assert.True(background.IsCancellationRequested);
            Assert.Null(fixture.Accounts.GetCaptureBinding(fixture.UserId));
            await fixture.Accounts.DisconnectAsync(Guid.NewGuid(), TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finish);
        await disconnect;
        Assert.Null(await fixture.Accounts.GetAsync(fixture.UserId, TestContext.Current.CancellationToken));
        Assert.Null(await fixture.Store.ReadAsync<AccountService.StoredAttempt>(fixture.UserId, "attempt", TestContext.Current.CancellationToken));
        fixture.Client.AsyncHandler = null;
        await fixture.ConnectAsync();
        Assert.Equal("TestListener", (await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Username);
    }

    [Fact]
    public async Task DisconnectInvalidatesPendingTokenRequestAndCancelledGateWaiters()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.AsyncHandler = async (_, _, token) =>
        {
            entered.SetResult(token);
            await release.Task;
            return JsonDocument.Parse("{\"token\":\"stale-token\"}");
        };
        var begin = fixture.Accounts.BeginAsync(fixture.UserId, TestContext.Current.CancellationToken);
        var authorization = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var second = fixture.Accounts.BeginAsync(fixture.UserId, waiting.Token);
        await waiting.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        var disconnect = fixture.Accounts.DisconnectAsync(fixture.UserId, TestContext.Current.CancellationToken);
        try { Assert.True(authorization.IsCancellationRequested); }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => begin);
        await disconnect;
        Assert.Null(await fixture.Store.ReadAsync<AccountService.StoredAttempt>(fixture.UserId, "attempt", TestContext.Current.CancellationToken));
        fixture.Client.AsyncHandler = null;
        await fixture.ConnectAsync();
        Assert.False((await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).NeedsReconnect);
    }

    [Fact]
    public async Task SequentialUsersDoNotExhaustConcurrentMutationLeaseCapacity()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        for (var index = 0; index < 300; index++)
        {
            var userId = Guid.NewGuid();
            var attempt = await fixture.Accounts.BeginAsync(userId, TestContext.Current.CancellationToken);
            Assert.NotEqual(Guid.Empty, attempt.AttemptId);
            await fixture.Accounts.DisconnectAsync(userId, TestContext.Current.CancellationToken);
        }
        Assert.True((await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Connected);
    }
}
