using JellySin.Plugin.Lastfm.Configuration;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class AccountTests
{
    [Fact]
    public async Task BrowserGrantBindsAttemptToAuthenticatedUser()
    {
        using var fixture = new CoreFixture();
        var attempt = await fixture.Accounts.BeginAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.StartsWith("https://www.last.fm/api/auth/", attempt.AuthorizationUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("browser-test-token", attempt.ToString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Accounts.FinishAsync(Guid.NewGuid(), attempt.AttemptId, TestContext.Current.CancellationToken));
        var status = await fixture.Accounts.FinishAsync(fixture.UserId, attempt.AttemptId, TestContext.Current.CancellationToken);
        Assert.True(status.Connected);
        Assert.Equal("TestListener", status.Username);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Accounts.FinishAsync(fixture.UserId, attempt.AttemptId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExpiredAttemptsCannotBeExchanged()
    {
        using var fixture = new CoreFixture();
        var attempt = await fixture.Accounts.BeginAsync(fixture.UserId, TestContext.Current.CancellationToken);
        fixture.Clock.Advance(601);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Accounts.FinishAsync(fixture.UserId, attempt.AttemptId, TestContext.Current.CancellationToken));
        Assert.Single(fixture.Client.Calls);
    }

    [Fact]
    public async Task SessionsAndApplicationSecretsAreProtectedOnDisk()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        foreach (var file in Directory.EnumerateFiles(fixture.DirectoryPath, "*.json", SearchOption.AllDirectories))
        {
            var content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("private-session-value", content, StringComparison.Ordinal);
            Assert.DoesNotContain(new string('b', 32), content, StringComparison.Ordinal);
        }
        Assert.Equal("private-session-value", (await fixture.Accounts.GetAsync(fixture.UserId, TestContext.Current.CancellationToken))!.SessionKey);
        Assert.DoesNotContain("private-session-value", (await fixture.Accounts.GetAsync(fixture.UserId, TestContext.Current.CancellationToken))!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisconnectCancelsOperationsAndDeletesPrivateState()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var cancellation = fixture.Accounts.GetOperationToken(fixture.UserId);
        await fixture.Store.WriteAsync(fixture.UserId, "feature-history", "personal data", TestContext.Current.CancellationToken);
        await fixture.Accounts.DisconnectAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(fixture.Accounts.GetOperationToken(fixture.UserId).IsCancellationRequested);
        Assert.False((await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Connected);
        Assert.Null(await fixture.Store.ReadAsync<string>(fixture.UserId, "feature-history", TestContext.Current.CancellationToken));
        await fixture.ConnectAsync();
        Assert.False(fixture.Accounts.GetOperationToken(fixture.UserId).IsCancellationRequested);
    }

    [Fact]
    public async Task ReconnectionPreservesScrobblingPreference()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        await fixture.Accounts.SetScrobblingAsync(fixture.UserId, false, TestContext.Current.CancellationToken);
        await fixture.Accounts.MarkReconnectAsync(fixture.UserId, TestContext.Current.CancellationToken);
        await fixture.ConnectAsync();
        var status = await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.False(status.NeedsReconnect);
        Assert.False(status.ScrobblingEnabled);
    }

    [Fact]
    public async Task NewAttemptReplacesOldAttempt()
    {
        using var fixture = new CoreFixture();
        var first = await fixture.Accounts.BeginAsync(fixture.UserId, TestContext.Current.CancellationToken);
        var second = await fixture.Accounts.BeginAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.NotEqual(first.AttemptId, second.AttemptId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Accounts.FinishAsync(fixture.UserId, first.AttemptId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ProtectedCredentialsSurviveProviderRestart()
    {
        using var fixture = new CoreFixture();
        var first = new ApplicationCredentialService(fixture.Store, new LastfmProtection(Path.Combine(fixture.DirectoryPath, "keys")).Provider);
        await first.SetAsync(new ApplicationCredentials(new string('a', 32), new string('b', 32)), TestContext.Current.CancellationToken);
        var restarted = new ApplicationCredentialService(fixture.Store, new LastfmProtection(Path.Combine(fixture.DirectoryPath, "keys")).Provider);
        Assert.Equal(new string('b', 32), (await restarted.GetAsync(TestContext.Current.CancellationToken)).Secret);
    }

    [Fact]
    public async Task ChangingLastfmAccountDeletesPreviousPrivateHistory()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var oldOperations = fixture.Accounts.GetOperationToken(fixture.UserId);
        await fixture.Store.WriteAsync(fixture.UserId, "feature-history", "first user's history", TestContext.Current.CancellationToken);
        fixture.Client.Handler = (method, _, _) => System.Text.Json.JsonDocument.Parse(method == "auth.getToken"
            ? "{\"token\":\"second-token\"}" : "{\"session\":{\"name\":\"AnotherListener\",\"key\":\"different-session\"}}");
        var attempt = await fixture.Accounts.BeginAsync(fixture.UserId, TestContext.Current.CancellationToken);
        await fixture.Accounts.FinishAsync(fixture.UserId, attempt.AttemptId, TestContext.Current.CancellationToken);
        Assert.Null(await fixture.Store.ReadAsync<string>(fixture.UserId, "feature-history", TestContext.Current.CancellationToken));
        Assert.True(oldOperations.IsCancellationRequested);
        Assert.Equal("AnotherListener", (await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Username);
    }

    [Theory]
    [InlineData("bad", "also bad")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaz", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public async Task InvalidApplicationCredentialsAreRejected(string key, string secret)
    {
        using var fixture = new CoreFixture();
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Credentials.SetAsync(new ApplicationCredentials(key, secret), TestContext.Current.CancellationToken));
    }
}
