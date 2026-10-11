using System.Web;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Transport;
using Microsoft.AspNetCore.DataProtection;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class ConnectionApprovalTests
{
    private const string Token = "browser-test-token";

    [Fact]
    public async Task SignInStartsWithoutContactingLastfmAndNamesTheCallback()
    {
        using var fixture = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        var attempt = await fixture.Accounts.BeginAsync(fixture.UserId, CoreFixture.Callback, false, TestContext.Current.CancellationToken);
        Assert.Empty(fixture.Client.Calls);
        var address = new Uri(attempt.AuthorizationUrl);
        Assert.Equal("https://www.last.fm/api/auth/", address.GetLeftPart(UriPartial.Path));
        var query = HttpUtility.ParseQueryString(address.Query);
        Assert.Equal(new string?[] { "api_key", "cb" }, query.AllKeys);
        Assert.Equal(new string('a', 32), query["api_key"]);
        var stored = await fixture.Store.ReadAsync<AccountService.StoredAttempt>(fixture.UserId, "attempt", TestContext.Current.CancellationToken);
        Assert.Equal($"{CoreFixture.Callback}?attempt={stored!.Id:N}", query["cb"]);
        Assert.Equal(fixture.Clock.GetUtcNow().AddMinutes(10), attempt.ExpiresAt);
        Assert.NotEmpty(attempt.BrowserBinding);
        Assert.DoesNotContain(attempt.BrowserBinding, attempt.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(attempt.BrowserBinding, attempt.AuthorizationUrl, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('b', 32), attempt.AuthorizationUrl, StringComparison.Ordinal);
        Assert.False((await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).ApprovalPending);
    }

    [Fact]
    public async Task SignInCannotStartWithoutAnApplication()
    {
        using var fixture = new CoreFixture();
        Assert.False((await fixture.Credentials.GetAsync(TestContext.Current.CancellationToken)).IsConfigured);
        var error = await Assert.ThrowsAsync<LastfmException>(() => fixture.Accounts.BeginAsync(fixture.UserId, CoreFixture.Callback, false, TestContext.Current.CancellationToken));
        Assert.Equal(10, error.Code);
        Assert.Null(await fixture.Store.ReadAsync<AccountService.StoredAttempt>(fixture.UserId, "attempt", TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Client.Calls);
    }

    [Theory]
    [InlineData("ftp://jellyfin.example/JellySin/Lastfm/Callback")]
    [InlineData("https://jellyfin.example/JellySin/Lastfm/Callback?next=elsewhere")]
    [InlineData("https://jellyfin.example/JellySin/Lastfm/Callback#fragment")]
    [InlineData("https://listener:secret@jellyfin.example/JellySin/Lastfm/Callback")]
    public async Task SignInRefusesACallbackThatIsNotAPlainWebAddress(string callback)
    {
        using var fixture = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Accounts.BeginAsync(fixture.UserId, new Uri(callback), false, TestContext.Current.CancellationToken));
        Assert.Null(await fixture.Store.ReadAsync<AccountService.StoredAttempt>(fixture.UserId, "attempt", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApprovalNeedsTheBrowserThatStartedTheAttempt()
    {
        using var fixture = new CoreFixture();
        using var elsewhere = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        await elsewhere.ConfigureApplicationAsync();
        var attempt = await fixture.Accounts.BeginAsync(fixture.UserId, CoreFixture.Callback, false, TestContext.Current.CancellationToken);
        var foreign = await elsewhere.Accounts.BeginAsync(fixture.UserId, CoreFixture.Callback, false, TestContext.Current.CancellationToken);
        var id = CoreFixture.AttemptOf(attempt);
        var flipped = attempt.BrowserBinding[..20] + (attempt.BrowserBinding[20] == 'A' ? 'B' : 'A') + attempt.BrowserBinding[21..];
        // Without a readable cookie nothing names a user, so the refusal names none either.
        foreach (var binding in new[] { null, "" })
            Assert.Equal(new ConnectionApproval(false, false, ApprovalRefusal.NoCookie), await fixture.Accounts.ApproveAsync(binding, id, Token, TestContext.Current.CancellationToken));
        foreach (var binding in new[] { id, flipped, foreign.BrowserBinding, new string('A', 5000) })
            Assert.Equal(new ConnectionApproval(false, false, ApprovalRefusal.UnreadableCookie), await fixture.Accounts.ApproveAsync(binding, id, Token, TestContext.Current.CancellationToken));
        Assert.False((await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).ApprovalPending);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Accounts.FinishAsync(fixture.UserId, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Client.Calls);
        Assert.True((await fixture.Accounts.ApproveAsync(attempt.BrowserBinding, id, Token, TestContext.Current.CancellationToken)).Accepted);
    }

    [Fact]
    public async Task ApprovalReachesOnlyTheUserWhoStartedIt()
    {
        using var fixture = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        var other = Guid.NewGuid();
        var mine = await fixture.Accounts.BeginAsync(fixture.UserId, CoreFixture.Callback, false, TestContext.Current.CancellationToken);
        var theirs = await fixture.Accounts.BeginAsync(other, CoreFixture.Callback, false, TestContext.Current.CancellationToken);
        Assert.Equal(ApprovalRefusal.OtherAttempt, (await fixture.Accounts.ApproveAsync(theirs.BrowserBinding, CoreFixture.AttemptOf(mine), Token, TestContext.Current.CancellationToken)).Refusal);
        Assert.Equal(ApprovalRefusal.OtherAttempt, (await fixture.Accounts.ApproveAsync(mine.BrowserBinding, CoreFixture.AttemptOf(theirs), Token, TestContext.Current.CancellationToken)).Refusal);
        Assert.True((await fixture.Accounts.ApproveAsync(mine.BrowserBinding, CoreFixture.AttemptOf(mine), Token, TestContext.Current.CancellationToken)).Accepted);
        Assert.True((await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).ApprovalPending);
        Assert.False((await fixture.Accounts.GetStatusAsync(other, TestContext.Current.CancellationToken)).ApprovalPending);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Accounts.FinishAsync(other, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Client.Calls);
        var status = await fixture.Accounts.FinishAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.True(status.Connected);
        Assert.False(status.ApprovalPending);
        Assert.Equal("TestListener", status.Username);
        Assert.False((await fixture.Accounts.GetStatusAsync(other, TestContext.Current.CancellationToken)).Connected);
        var call = Assert.Single(fixture.Client.Calls);
        Assert.Equal("auth.getSession", call.Method);
        Assert.Equal(Token, call.Values["token"]);
    }

    [Fact]
    public async Task AttemptsEndAfterTenMinutes()
    {
        using var fixture = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        var late = await fixture.Accounts.BeginAsync(fixture.UserId, CoreFixture.Callback, false, TestContext.Current.CancellationToken);
        fixture.Clock.Advance(600);
        Assert.Equal(ApprovalRefusal.Expired, (await fixture.Accounts.ApproveAsync(late.BrowserBinding, CoreFixture.AttemptOf(late), Token, TestContext.Current.CancellationToken)).Refusal);
        await fixture.ApproveAsync(fixture.UserId);
        fixture.Clock.Advance(599);
        Assert.True((await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).ApprovalPending);
        fixture.Clock.Advance(1);
        Assert.False((await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).ApprovalPending);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Accounts.FinishAsync(fixture.UserId, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Client.Calls);
    }

    [Fact]
    public async Task AnAttemptIsApprovedAndExchangedOnce()
    {
        using var fixture = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        var attempt = await fixture.ApproveAsync(fixture.UserId, "first-token");
        var id = CoreFixture.AttemptOf(attempt);
        Assert.Equal(ApprovalRefusal.Used, (await fixture.Accounts.ApproveAsync(attempt.BrowserBinding, id, "second-token", TestContext.Current.CancellationToken)).Refusal);
        Assert.True((await fixture.Accounts.FinishAsync(fixture.UserId, TestContext.Current.CancellationToken)).Connected);
        Assert.Equal("first-token", Assert.Single(fixture.Client.Calls).Values["token"]);
        Assert.Equal(ApprovalRefusal.Replaced, (await fixture.Accounts.ApproveAsync(attempt.BrowserBinding, id, "third-token", TestContext.Current.CancellationToken)).Refusal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Accounts.FinishAsync(fixture.UserId, TestContext.Current.CancellationToken));
        Assert.Single(fixture.Client.Calls);
    }

    [Fact]
    public async Task ANewAttemptReplacesTheOldOne()
    {
        using var fixture = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        var first = await fixture.Accounts.BeginAsync(fixture.UserId, CoreFixture.Callback, false, TestContext.Current.CancellationToken);
        var second = await fixture.Accounts.BeginAsync(fixture.UserId, CoreFixture.Callback, false, TestContext.Current.CancellationToken);
        Assert.NotEqual(CoreFixture.AttemptOf(first), CoreFixture.AttemptOf(second));
        Assert.Equal(ApprovalRefusal.Replaced, (await fixture.Accounts.ApproveAsync(first.BrowserBinding, CoreFixture.AttemptOf(first), Token, TestContext.Current.CancellationToken)).Refusal);
        Assert.True((await fixture.Accounts.ApproveAsync(second.BrowserBinding, CoreFixture.AttemptOf(second), Token, TestContext.Current.CancellationToken)).Accepted);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("has space")]
    [InlineData("two&values=1")]
    [InlineData("line\nbreak")]
    [InlineData("tökén")]
    public async Task ApprovalRefusesMalformedTokensAndAttemptNames(string? token)
    {
        using var fixture = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        var attempt = await fixture.Accounts.BeginAsync(fixture.UserId, CoreFixture.Callback, true, TestContext.Current.CancellationToken);
        var id = CoreFixture.AttemptOf(attempt);
        var refused = await fixture.Accounts.ApproveAsync(attempt.BrowserBinding, id, token, TestContext.Current.CancellationToken);
        Assert.Equal(new ConnectionApproval(false, true, ApprovalRefusal.NoToken, fixture.UserId), refused);
        Assert.Equal(ApprovalRefusal.NoToken, (await fixture.Accounts.ApproveAsync(attempt.BrowserBinding, id, new string('a', 257), TestContext.Current.CancellationToken)).Refusal);
        foreach (var name in new[] { null, "", "not-an-attempt", Guid.NewGuid().ToString("N"), id + "0" })
            Assert.Equal(ApprovalRefusal.OtherAttempt, (await fixture.Accounts.ApproveAsync(attempt.BrowserBinding, name, Token, TestContext.Current.CancellationToken)).Refusal);
        Assert.False((await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).ApprovalPending);
        Assert.Equal(new ConnectionApproval(true, true, null, fixture.UserId), await fixture.Accounts.ApproveAsync(attempt.BrowserBinding, id, new string('a', 256), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AServerRestartEndsOpenAttempts()
    {
        using var fixture = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        var open = await fixture.Accounts.BeginAsync(fixture.UserId, CoreFixture.Callback, false, TestContext.Current.CancellationToken);
        var other = Guid.NewGuid();
        await fixture.ApproveAsync(other);
        using var restarted = new AccountService(fixture.Store, fixture.Client, fixture.Credentials, fixture.Protection, fixture.Clock);
        Assert.Equal(ApprovalRefusal.NotOpen, (await restarted.ApproveAsync(open.BrowserBinding, CoreFixture.AttemptOf(open), Token, TestContext.Current.CancellationToken)).Refusal);
        // Background work may reach the user before the browser returns; the attempt of the earlier process stays refused.
        _ = restarted.GetOperationToken(fixture.UserId);
        Assert.Equal(ApprovalRefusal.AccountChanged, (await restarted.ApproveAsync(open.BrowserBinding, CoreFixture.AttemptOf(open), Token, TestContext.Current.CancellationToken)).Refusal);
        Assert.False((await restarted.GetStatusAsync(other, TestContext.Current.CancellationToken)).ApprovalPending);
        _ = restarted.GetOperationToken(other);
        Assert.False((await restarted.GetStatusAsync(other, TestContext.Current.CancellationToken)).ApprovalPending);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.FinishAsync(other, TestContext.Current.CancellationToken));
        Assert.Empty(fixture.Client.Calls);
        var again = await restarted.BeginAsync(fixture.UserId, CoreFixture.Callback, false, TestContext.Current.CancellationToken);
        Assert.True((await restarted.ApproveAsync(again.BrowserBinding, CoreFixture.AttemptOf(again), Token, TestContext.Current.CancellationToken)).Accepted);
        Assert.True((await restarted.FinishAsync(fixture.UserId, TestContext.Current.CancellationToken)).Connected);
    }

    [Fact]
    public async Task DisconnectEndsAnApprovedAttempt()
    {
        using var fixture = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        var attempt = await fixture.ApproveAsync(fixture.UserId);
        await fixture.Accounts.DisconnectAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.False((await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).ApprovalPending);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Accounts.FinishAsync(fixture.UserId, TestContext.Current.CancellationToken));
        Assert.Equal(ApprovalRefusal.Replaced, (await fixture.Accounts.ApproveAsync(attempt.BrowserBinding, CoreFixture.AttemptOf(attempt), Token, TestContext.Current.CancellationToken)).Refusal);
        Assert.Empty(fixture.Client.Calls);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(14)]
    [InlineData(15)]
    public async Task ATokenThatLastfmRefusesEndsTheAttempt(int code)
    {
        using var fixture = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        await fixture.ApproveAsync(fixture.UserId);
        fixture.Client.Handler = (_, _, _) => throw new LastfmException(code);
        await Assert.ThrowsAsync<LastfmException>(() => fixture.Accounts.FinishAsync(fixture.UserId, TestContext.Current.CancellationToken));
        Assert.Null(await fixture.Store.ReadAsync<AccountService.StoredAttempt>(fixture.UserId, "attempt", TestContext.Current.CancellationToken));
        Assert.False((await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).ApprovalPending);
    }

    [Fact]
    public async Task ATemporaryLastfmFailureKeepsTheApprovalForAnotherTry()
    {
        using var fixture = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        await fixture.ApproveAsync(fixture.UserId);
        fixture.Client.Handler = (_, _, _) => throw new LastfmException(16);
        await Assert.ThrowsAsync<LastfmException>(() => fixture.Accounts.FinishAsync(fixture.UserId, TestContext.Current.CancellationToken));
        Assert.True((await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).ApprovalPending);
        fixture.Client.Handler = null;
        Assert.True((await fixture.Accounts.FinishAsync(fixture.UserId, TestContext.Current.CancellationToken)).Connected);
    }

    [Fact]
    public async Task TheApprovedTokenIsProtectedOnDiskAndUnreadableStateIsRefused()
    {
        using var fixture = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        var attempt = await fixture.ApproveAsync(fixture.UserId, "token-that-must-not-reach-disk");
        foreach (var file in Directory.EnumerateFiles(fixture.DirectoryPath, "*.json", SearchOption.AllDirectories))
            Assert.DoesNotContain("token-that-must-not-reach-disk", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        await fixture.Store.WriteAsync(fixture.UserId, "attempt", "not an attempt document", TestContext.Current.CancellationToken);
        Assert.False((await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).ApprovalPending);
        Assert.Equal(ApprovalRefusal.NotStored, (await fixture.Accounts.ApproveAsync(attempt.BrowserBinding, CoreFixture.AttemptOf(attempt), Token, TestContext.Current.CancellationToken)).Refusal);
        Assert.Empty(fixture.Client.Calls);
    }

    [Fact]
    public async Task ABindingProtectedForAnotherPurposeIsRefused()
    {
        using var fixture = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        var attempt = await fixture.Accounts.BeginAsync(fixture.UserId, CoreFixture.Callback, false, TestContext.Current.CancellationToken);
        var id = CoreFixture.AttemptOf(attempt);
        var session = fixture.Protection.CreateProtector("JellySin.Lastfm.Account.v1").Protect($"{fixture.UserId:N}{id}0");
        Assert.Equal(ApprovalRefusal.UnreadableCookie, (await fixture.Accounts.ApproveAsync(session, id, Token, TestContext.Current.CancellationToken)).Refusal);
        Assert.False((await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).ApprovalPending);
    }

    [Fact]
    public async Task AnOverlongBindingIsRefusedBeforeAnyCryptographicWork()
    {
        using var fixture = new CoreFixture();
        var protection = new CountingProtection(fixture.Protection);
        using var accounts = new AccountService(fixture.Store, fixture.Client, fixture.Credentials, protection, fixture.Clock);
        // Both values decode as text of the right alphabet; only the first is within the bound and reaches the protector.
        Assert.Equal(ApprovalRefusal.UnreadableCookie, (await accounts.ApproveAsync(new string('A', 1024), Guid.NewGuid().ToString("N"), Token, TestContext.Current.CancellationToken)).Refusal);
        Assert.Equal(1, protection.Unprotected);
        Assert.Equal(ApprovalRefusal.UnreadableCookie, (await accounts.ApproveAsync(new string('A', 1028), Guid.NewGuid().ToString("N"), Token, TestContext.Current.CancellationToken)).Refusal);
        Assert.Equal(1, protection.Unprotected);
    }

    private sealed class CountingProtection(IDataProtectionProvider inner) : IDataProtectionProvider
    {
        private int _unprotected;
        public int Unprotected => _unprotected;
        public IDataProtector CreateProtector(string purpose) => new Counter(this, inner.CreateProtector(purpose));

        private sealed class Counter(CountingProtection owner, IDataProtector inner) : IDataProtector
        {
            public IDataProtector CreateProtector(string purpose) => new Counter(owner, inner.CreateProtector(purpose));
            public byte[] Protect(byte[] plaintext) => inner.Protect(plaintext);
            public byte[] Unprotect(byte[] protectedData)
            {
                Interlocked.Increment(ref owner._unprotected);
                return inner.Unprotect(protectedData);
            }
        }
    }
}
