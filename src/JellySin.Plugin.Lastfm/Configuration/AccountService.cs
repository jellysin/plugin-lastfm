using System.Collections.Concurrent;
using JellySin.Plugin.Lastfm.Playback;
using JellySin.Plugin.Lastfm.Storage;
using JellySin.Plugin.Lastfm.Transport;
using Microsoft.AspNetCore.DataProtection;

namespace JellySin.Plugin.Lastfm.Configuration;

public sealed record LinkedAccount(string Username, string SessionKey, bool NeedsReconnect, bool ScrobblingEnabled)
{
    public override string ToString() => "Linked Last.fm account (credentials redacted)";
}

public sealed record AccountStatus(bool Connected, string? Username, bool NeedsReconnect, bool ScrobblingEnabled);

public sealed record ConnectionAttempt(Guid AttemptId, string AuthorizationUrl, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "Last.fm browser authorization attempt (URL redacted)";
}

public sealed class AccountService(
    IStateStore store,
    ILastfmClient client,
    ApplicationCredentialService credentials,
    IDataProtectionProvider protection,
    TimeProvider clock) : IDisposable
{
    private readonly IDataProtector _protector = protection.CreateProtector("JellySin.Lastfm.Account.v1");
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _operations = new();

    public CancellationToken GetOperationToken(Guid userId)
    {
        RequireUser(userId);
        if (_operations.Count >= 8192 && !_operations.ContainsKey(userId)) throw new InvalidOperationException("Too many connected accounts.");
        return _operations.GetOrAdd(userId, _ => new CancellationTokenSource()).Token;
    }

    public async Task<ConnectionAttempt> BeginAsync(Guid userId, CancellationToken cancellationToken)
    {
        RequireUser(userId);
        using var response = await client.CallAsync("auth.getToken", new Dictionary<string, string>(), null, RequestPriority.Interactive, cancellationToken).ConfigureAwait(false);
        var token = response.RootElement.GetProperty("token").GetString();
        if (string.IsNullOrEmpty(token) || token.Length > 256) throw new LastfmException(8);
        var attempt = new StoredAttempt(Guid.NewGuid(), _protector.Protect(token), clock.GetUtcNow().AddMinutes(10));
        await store.WriteAsync(userId, "attempt", attempt, cancellationToken).ConfigureAwait(false);
        var app = await credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        return new ConnectionAttempt(attempt.Id, $"https://www.last.fm/api/auth/?api_key={Uri.EscapeDataString(app.ApiKey)}&token={Uri.EscapeDataString(token)}", attempt.ExpiresAt);
    }

    public async Task<AccountStatus> FinishAsync(Guid userId, Guid attemptId, CancellationToken cancellationToken)
    {
        RequireUser(userId);
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = await store.ReadAsync<StoredAttempt>(userId, "attempt", cancellationToken).ConfigureAwait(false);
            if (pending is null || pending.Id != attemptId || pending.ExpiresAt <= clock.GetUtcNow())
                throw new InvalidOperationException("Connection attempt is missing or expired. Start again.");
            using var response = await client.CallAsync("auth.getSession", new Dictionary<string, string> { ["token"] = _protector.Unprotect(pending.ProtectedToken) }, null, RequestPriority.Interactive, cancellationToken).ConfigureAwait(false);
            var session = response.RootElement.GetProperty("session");
            var username = session.GetProperty("name").GetString();
            var key = session.GetProperty("key").GetString();
            if (string.IsNullOrEmpty(username) || username.Length > 256 || string.IsNullOrEmpty(key) || key.Length > 256)
                throw new LastfmException(8);
            var previous = await GetAsync(userId, cancellationToken).ConfigureAwait(false);
            CancelOperations(userId);
            if (previous is not null && !string.Equals(previous.Username, username, StringComparison.OrdinalIgnoreCase))
            {
                await store.DeleteUserAsync(userId, cancellationToken).ConfigureAwait(false);
            }
            await store.WriteAsync(userId, "account", new StoredAccount(username, _protector.Protect(key), false, previous?.ScrobblingEnabled ?? true), cancellationToken).ConfigureAwait(false);
            var outbox = await store.ReadAsync<OutboxState>(userId, "outbox", cancellationToken).ConfigureAwait(false);
            if (outbox is not null)
                await store.UpdateAsync<OutboxState>(userId, "outbox", current => (current ?? outbox) with
                {
                    Pending = (current ?? outbox).Pending.Select(item => item.BlockedCode == 9 ? item with { BlockedCode = null, NextAttemptAt = null } : item).ToList()
                }, cancellationToken).ConfigureAwait(false);
            await store.DeleteAsync(userId, "attempt", cancellationToken).ConfigureAwait(false);
            var cancelledOperations = _operations[userId];
            _operations[userId] = new CancellationTokenSource();
            cancelledOperations.Dispose();
            return new AccountStatus(true, username, false, previous?.ScrobblingEnabled ?? true);
        }
        catch (LastfmException exception) when (exception.Code is 4 or 15)
        {
            await store.DeleteAsync(userId, "attempt", cancellationToken).ConfigureAwait(false);
            throw;
        }
        finally { _connectionGate.Release(); }
    }

    public async Task<LinkedAccount?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        RequireUser(userId);
        var account = await store.ReadAsync<StoredAccount>(userId, "account", cancellationToken).ConfigureAwait(false);
        return account is null ? null : new LinkedAccount(account.Username, _protector.Unprotect(account.ProtectedSession), account.NeedsReconnect, account.ScrobblingEnabled);
    }

    public async Task<AccountStatus> GetStatusAsync(Guid userId, CancellationToken cancellationToken)
    {
        var account = await GetAsync(userId, cancellationToken).ConfigureAwait(false);
        return new AccountStatus(account is not null, account?.Username, account?.NeedsReconnect ?? false, account?.ScrobblingEnabled ?? false);
    }

    public async Task<IReadOnlyList<Guid>> GetAccountsAsync(CancellationToken cancellationToken)
    {
        var users = await store.GetUsersAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<Guid>();
        foreach (var id in users)
            if (await store.ReadAsync<StoredAccount>(id, "account", cancellationToken).ConfigureAwait(false) is not null) result.Add(id);
        return result;
    }

    public async Task DisconnectAsync(Guid userId, CancellationToken cancellationToken)
    {
        RequireUser(userId);
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { CancelOperations(userId); await store.DeleteUserAsync(userId, cancellationToken).ConfigureAwait(false); }
        finally { _connectionGate.Release(); }
    }

    public Task SetScrobblingAsync(Guid userId, bool enabled, CancellationToken cancellationToken)
        => store.UpdateAsync<StoredAccount>(userId, "account", current => (current ?? throw new InvalidOperationException("Connect Last.fm first.")) with { ScrobblingEnabled = enabled }, cancellationToken);

    public Task MarkReconnectAsync(Guid userId, CancellationToken cancellationToken)
        => store.UpdateAsync<StoredAccount>(userId, "account", current => (current ?? throw new InvalidOperationException("Account disconnected.")) with { NeedsReconnect = true }, cancellationToken);

    private static void RequireUser(Guid userId)
    {
        if (userId == Guid.Empty) throw new ArgumentException("An authenticated user is required.", nameof(userId));
    }

    private void CancelOperations(Guid userId)
    {
        // Retain the cancelled source until a successful reconnect. Removing it here
        // would let a racing background task obtain a new live token after disconnect.
        _operations.GetOrAdd(userId, _ => new CancellationTokenSource()).Cancel();
    }

    public void Dispose()
    {
        foreach (var source in _operations.Values) { source.Cancel(); source.Dispose(); }
        _operations.Clear();
        _connectionGate.Dispose();
    }

    public sealed record StoredAccount(string Username, string ProtectedSession, bool NeedsReconnect, bool ScrobblingEnabled);

    public sealed record StoredAttempt(Guid Id, string ProtectedToken, DateTimeOffset ExpiresAt);
}
