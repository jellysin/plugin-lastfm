using System.Text.Json;
using JellySin.Plugin.Lastfm.Configuration;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class AccountConcurrencyTests
{
    [Fact]
    public async Task DisconnectImmediatelyCancelsPendingGrantAndCannotBeUndoneByItsResponse()
    {
        using var fixture = new CoreFixture();
        var (_, finish, authorization, release) = await StartBlockedGrantAsync(fixture);
        var background = fixture.Accounts.GetOperationToken(fixture.UserId);
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
        await AssertGrantCancelledAndDisconnectedAsync(fixture, finish, disconnect);
        Assert.Null(await fixture.Store.ReadAsync<AccountService.StoredAttempt>(fixture.UserId, "attempt", TestContext.Current.CancellationToken));
        fixture.Client.AsyncHandler = null;
        await fixture.ConnectAsync();
        Assert.Equal("TestListener", (await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Username);
    }

    [Fact]
    public async Task DisconnectInvalidatesPendingExchangeAndCancelledGateWaiters()
    {
        using var fixture = new CoreFixture();
        var (attempt, finish, authorization, release) = await StartBlockedGrantAsync(fixture);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var second = fixture.Accounts.BeginAsync(fixture.UserId, CoreFixture.Callback, false, waiting.Token);
        await waiting.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        var disconnect = fixture.Accounts.DisconnectAsync(fixture.UserId, TestContext.Current.CancellationToken);
        try { Assert.True(authorization.IsCancellationRequested); }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finish);
        await disconnect;
        Assert.Null(await fixture.Store.ReadAsync<AccountService.StoredAttempt>(fixture.UserId, "attempt", TestContext.Current.CancellationToken));
        Assert.False((await fixture.Accounts.ApproveAsync(attempt.BrowserBinding, CoreFixture.AttemptOf(attempt), "late-token", TestContext.Current.CancellationToken)).Accepted);
        fixture.Client.AsyncHandler = null;
        await fixture.ConnectAsync();
        Assert.False((await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).NeedsReconnect);
    }

    /// <summary>Cancelled scrobbling waits return their waiter slots and leave the account gate free.</summary>
    [Fact]
    public async Task CancelledScrobblingWaitLeavesTheAccountGateFree()
    {
        using var fixture = new CoreFixture();
        var (_, finish, _, release) = await StartBlockedGrantAsync(fixture);
        const int ordinaryWaiterLimit = 8; // AccountGatePool's per-user limit includes the holder.
        try
        {
            for (var round = 0; round < ordinaryWaiterLimit; round++)
            {
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                var change = fixture.Accounts.SetScrobblingAsync(fixture.UserId, false, waiting.Token);
                await waiting.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => change);
            }
        }
        finally
        {
            release.TrySetResult();
        }
        await finish;
        await fixture.Accounts.SetScrobblingAsync(fixture.UserId, false, TestContext.Current.CancellationToken);
        Assert.False((await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).ScrobblingEnabled);
    }

    /// <summary>A scrobbling change waits for a pending grant and is cancelled when the account disconnects.</summary>
    [Fact]
    public async Task ScrobblingChangeWaitsForAPendingGrantAndEndsWithDisconnect()
    {
        using var fixture = new CoreFixture();
        var (_, finish, _, release) = await StartBlockedGrantAsync(fixture);
        var change = fixture.Accounts.SetScrobblingAsync(fixture.UserId, false, TestContext.Current.CancellationToken);
        Task disconnect;
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => change.WaitAsync(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken));
            disconnect = fixture.Accounts.DisconnectAsync(fixture.UserId, TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => change.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        }
        finally
        {
            release.TrySetResult();
        }
        await AssertGrantCancelledAndDisconnectedAsync(fixture, finish, disconnect);
    }

    [Fact]
    public async Task SequentialUsersDoNotExhaustConcurrentMutationLeaseCapacity()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        for (var index = 0; index < 300; index++)
        {
            var userId = Guid.NewGuid();
            var attempt = await fixture.Accounts.BeginAsync(userId, CoreFixture.Callback, false, TestContext.Current.CancellationToken);
            Assert.NotEmpty(attempt.BrowserBinding);
            await fixture.Accounts.DisconnectAsync(userId, TestContext.Current.CancellationToken);
        }
        Assert.True((await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Connected);
    }

    private static async Task<(ConnectionAttempt Attempt, Task<AccountStatus> Finish, CancellationToken Authorization, TaskCompletionSource Release)>
        StartBlockedGrantAsync(CoreFixture fixture)
    {
        await fixture.ConnectAsync();
        var attempt = await fixture.ApproveAsync(fixture.UserId);
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Client.AsyncHandler = async (_, _, token) =>
        {
            entered.SetResult(token);
            // Deliberately emulate a transport which delivers its response after cancellation.
            await release.Task;
            return JsonDocument.Parse("{\"session\":{\"name\":\"StaleListener\",\"key\":\"stale-key\"}}");
        };
        var finish = fixture.Accounts.FinishAsync(fixture.UserId, TestContext.Current.CancellationToken);
        var authorization = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        return (attempt, finish, authorization, release);
    }

    /// <summary>Checks that the released grant ended cancelled and that the disconnect removed the account.</summary>
    private static async Task AssertGrantCancelledAndDisconnectedAsync(CoreFixture fixture, Task finish, Task disconnect)
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => finish);
        await disconnect;
        Assert.Null(await fixture.Accounts.GetAsync(fixture.UserId, TestContext.Current.CancellationToken));
    }
}
