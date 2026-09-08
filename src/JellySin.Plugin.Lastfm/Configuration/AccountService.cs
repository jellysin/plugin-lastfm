using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using JellySin.Plugin.Lastfm.Playback;
using JellySin.Plugin.Lastfm.Storage;
using JellySin.Plugin.Lastfm.Transport;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.DataProtection;

namespace JellySin.Plugin.Lastfm.Configuration;

public sealed record LinkedAccount(string Username, string SessionKey, bool NeedsReconnect, bool ScrobblingEnabled, Guid Generation = default, string ApplicationIdentity = "")
{
    public override string ToString() => "Linked Last.fm account (credentials redacted)";
}

public sealed record AccountStatus(bool Connected, string? Username, bool NeedsReconnect, bool ScrobblingEnabled);
public sealed record CaptureBinding(Guid AccountGeneration, Guid CaptureGeneration);

public sealed record ConnectionAttempt(Guid AttemptId, string AuthorizationUrl, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "Last.fm browser authorization attempt (URL redacted)";
}

public sealed class AccountService(
    IStateStore store,
    ILastfmClient client,
    ApplicationCredentialService credentials,
    IDataProtectionProvider protection,
    TimeProvider clock,
    IUserManager? users = null) : IDisposable
{
    private readonly IDataProtector _protector = protection.CreateProtector("JellySin.Lastfm.Account.v1");
    private readonly AccountGatePool _connectionGates = new();
    private readonly object _operationSync = new();
    private readonly ConcurrentDictionary<Guid, AccountOperation> _operations = new();
    private readonly ConcurrentDictionary<Guid, CaptureBinding> _captures = new();
    private readonly ConcurrentDictionary<Guid, bool> _hostSuspended = new();

    public CaptureBinding? GetCaptureBinding(Guid userId) => _captures.GetValueOrDefault(userId);

    public void SuspendHostUser(Guid userId)
    {
        if (!_operations.ContainsKey(userId) && !_captures.ContainsKey(userId)) return;
        _hostSuspended[userId] = true;
        GetOperation(userId).InvalidateAuthorization();
        _captures.TryRemove(userId, out _);
    }

    public async Task InitializeCapturesAsync(CancellationToken cancellationToken)
    {
        foreach (var userId in await GetAccountsAsync(cancellationToken).ConfigureAwait(false))
        {
            var operation = GetOperation(userId);
            var authorization = operation.Authorization;
            using var lease = await _connectionGates.AcquireAsync(userId, cancellationToken).ConfigureAwait(false);
            var account = await GetAsync(userId, cancellationToken).ConfigureAwait(false);
            if (account is { ScrobblingEnabled: true, NeedsReconnect: false } && !_hostSuspended.ContainsKey(userId))
            {
                try { operation.Publish(authorization, () => _captures.TryAdd(userId, new(account.Generation, Guid.NewGuid()))); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            }
        }
    }

    public CancellationToken GetOperationToken(Guid userId)
    {
        return GetOperation(userId).Token;
    }

    public async Task<ConnectionAttempt> BeginAsync(Guid userId, CancellationToken cancellationToken)
    {
        RequireUser(userId);
        RequireHostAccess(userId);
        var authorization = GetOperation(userId).Authorization;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorization.Token);
        cancellationToken = linked.Token;
        using var lease = await _connectionGates.AcquireAsync(userId, cancellationToken).ConfigureAwait(false);
        var app = await credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        var application = ApplicationCredentialService.Identity(app);
        using var response = await client.CallForApplicationAsync("auth.getToken", new Dictionary<string, string>(), null, application, RequestPriority.Interactive, cancellationToken).ConfigureAwait(false);
        var token = response.RootElement.GetProperty("token").GetString();
        if (string.IsNullOrEmpty(token) || token.Length > 256) throw new LastfmException(8);
        cancellationToken.ThrowIfCancellationRequested();
        RequireHostAccess(userId);
        var attempt = new StoredAttempt(Guid.NewGuid(), _protector.Protect(token), clock.GetUtcNow().AddMinutes(10), application, authorization.Id);
        await store.WriteAsync(userId, "attempt", attempt, cancellationToken).ConfigureAwait(false);
        return new ConnectionAttempt(attempt.Id, $"https://www.last.fm/api/auth/?api_key={Uri.EscapeDataString(app.ApiKey)}&token={Uri.EscapeDataString(token)}", attempt.ExpiresAt);
    }

    public async Task<AccountStatus> FinishAsync(Guid userId, Guid attemptId, CancellationToken cancellationToken)
    {
        RequireUser(userId);
        RequireHostAccess(userId);
        var operation = GetOperation(userId);
        var authorization = operation.Authorization;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorization.Token);
        cancellationToken = linked.Token;
        using var lease = await _connectionGates.AcquireAsync(userId, cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = await store.ReadAsync<StoredAttempt>(userId, "attempt", cancellationToken).ConfigureAwait(false);
            if (pending is null || pending.Id != attemptId || pending.ExpiresAt <= clock.GetUtcNow() || pending.Revision != authorization.Id)
                throw new InvalidOperationException("Connection attempt is missing or expired. Start again.");
            using var response = await client.CallForApplicationAsync("auth.getSession", new Dictionary<string, string> { ["token"] = _protector.Unprotect(pending.ProtectedToken) }, null, pending.ApplicationIdentity, RequestPriority.Interactive, cancellationToken).ConfigureAwait(false);
            var session = response.RootElement.GetProperty("session");
            var username = session.GetProperty("name").GetString();
            var key = session.GetProperty("key").GetString();
            if (string.IsNullOrEmpty(username) || username.Length > 256 || string.IsNullOrEmpty(key) || key.Length > 256)
                throw new LastfmException(8);
            cancellationToken.ThrowIfCancellationRequested();
            RequireHostAccess(userId);
            var previous = await GetAsync(userId, cancellationToken).ConfigureAwait(false);
            _captures.TryRemove(userId, out _);
            CancelOperations(userId);
            var sameAccount = previous is not null && string.Equals(previous.Username, username, StringComparison.OrdinalIgnoreCase);
            if (!sameAccount)
            {
                await store.DeleteUserAsync(userId, cancellationToken).ConfigureAwait(false);
            }
            var generation = sameAccount ? previous!.Generation : Guid.NewGuid();
            var application = pending.ApplicationIdentity;
            await store.WriteAsync(userId, "account", new StoredAccount(username, _protector.Protect(key), false, previous?.ScrobblingEnabled ?? true, generation, application), cancellationToken).ConfigureAwait(false);
            var outbox = await store.ReadAsync<OutboxState>(userId, "outbox", cancellationToken).ConfigureAwait(false);
            if (outbox is not null)
                await store.UpdateAsync<OutboxState>(userId, "outbox", current => (current ?? outbox) with
                {
                    Pending = (current ?? outbox).Pending.Select(item => item.BlockedCode == 9 ? item with { BlockedCode = null, NextAttemptAt = null } : item).ToList()
                }, cancellationToken).ConfigureAwait(false);
            await store.DeleteAsync(userId, "attempt", cancellationToken).ConfigureAwait(false);
            operation.Renew(authorization, () =>
            {
                _hostSuspended.TryRemove(userId, out _);
                if (previous?.ScrobblingEnabled ?? true) _captures[userId] = new(generation, Guid.NewGuid());
            });
            return await GetStatusAsync(userId, cancellationToken).ConfigureAwait(false);
        }
        catch (LastfmException exception) when (exception.Code is 4 or 15)
        {
            await store.DeleteAsync(userId, "attempt", cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<LinkedAccount?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        RequireUser(userId);
        StoredAccount? account;
        try { account = await store.ReadAsync<StoredAccount>(userId, "account", cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is JsonException or StorageBudgetException)
        {
            return UnreadableAccount(userId);
        }
        if (account is not null && (string.IsNullOrWhiteSpace(account.Username) || account.Username.Length > 256
            || string.IsNullOrEmpty(account.ProtectedSession) || account.ProtectedSession.Length > 16_384)) return UnreadableAccount(userId);
        if (account is not null && users is not null)
        {
            var user = users.GetUserById(userId);
            if (user is null || user.HasPermission(PermissionKind.IsDisabled))
            {
                SuspendHostUser(userId);
                if (user is null) await store.DeleteUserAsync(userId, cancellationToken).ConfigureAwait(false);
                return null;
            }
        }
        if (account is { Generation: var generation } && generation == Guid.Empty)
            account = await store.UpdateAsync<StoredAccount>(userId, "account", current =>
                (current ?? throw new AccountDisconnectedException()) with { Generation = current.Generation == Guid.Empty ? Guid.NewGuid() : current.Generation }, cancellationToken).ConfigureAwait(false);
        if (account is not null)
        {
            var application = await ApplicationIdentityAsync(cancellationToken).ConfigureAwait(false);
            if (account.ApplicationIdentity is null || account.ApplicationIdentity != application && !account.NeedsReconnect)
                account = await store.UpdateAsync<StoredAccount>(userId, "account", current =>
                    (current ?? throw new AccountDisconnectedException()) with
                    {
                        ApplicationIdentity = current.ApplicationIdentity ?? application,
                        NeedsReconnect = current.NeedsReconnect || current.ApplicationIdentity is not null && current.ApplicationIdentity != application
                    }, cancellationToken).ConfigureAwait(false);
        }
        if (account is null) return null;
        string session;
        try { session = _protector.Unprotect(account.ProtectedSession); }
        catch (CryptographicException) { session = string.Empty; }
        var cancelled = _operations.TryGetValue(userId, out var operations) && operations.IsCancellationRequested;
        return new LinkedAccount(account.Username, session, session.Length == 0 || account.NeedsReconnect || cancelled || _hostSuspended.ContainsKey(userId), account.ScrobblingEnabled, account.Generation, account.ApplicationIdentity!);
    }

    private LinkedAccount UnreadableAccount(Guid userId)
    {
        GetOperation(userId).Cancel();
        _captures.TryRemove(userId, out _);
        // Keep the account visible for recovery without trusting its unreadable identity.
        // A successful grant clears that identity's private state before replacement.
        return new LinkedAccount(string.Empty, string.Empty, true, false);
    }

    private bool HasHostAccess(Guid userId) => users is null || users.GetUserById(userId) is { } user && !user.HasPermission(PermissionKind.IsDisabled);

    private void RequireHostAccess(Guid userId)
    {
        if (!HasHostAccess(userId)) throw new UnauthorizedAccessException("The Jellyfin user is unavailable.");
    }

    private async Task<string> ApplicationIdentityAsync(CancellationToken cancellationToken)
        => ApplicationCredentialService.Identity(await credentials.GetAsync(cancellationToken).ConfigureAwait(false));

    public async Task<AccountStatus> GetStatusAsync(Guid userId, CancellationToken cancellationToken)
    {
        var account = await GetAsync(userId, cancellationToken).ConfigureAwait(false);
        return new AccountStatus(account is not null, string.IsNullOrEmpty(account?.Username) ? null : account.Username, account?.NeedsReconnect ?? false, account?.ScrobblingEnabled ?? false);
    }

    public async Task<IReadOnlyList<Guid>> GetAccountsAsync(CancellationToken cancellationToken)
    {
        var users = await store.GetUsersAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<Guid>();
        foreach (var id in users)
            if (await GetAsync(id, cancellationToken).ConfigureAwait(false) is not null) result.Add(id);
        return result;
    }

    public async Task DisconnectAsync(Guid userId, CancellationToken cancellationToken)
    {
        RequireUser(userId);
        var operation = GetOperation(userId);
        operation.InvalidateAuthorization();
        _captures.TryRemove(userId, out _);
        using var lease = await _connectionGates.AcquireAsync(userId, cancellationToken, cleanup: true).ConfigureAwait(false);
        operation.Cancel();
        _captures.TryRemove(userId, out _);
        await store.DeleteUserAsync(userId, cancellationToken).ConfigureAwait(false);
    }

    public async Task SetScrobblingAsync(Guid userId, bool enabled, CancellationToken cancellationToken)
    {
        RequireUser(userId);
        RequireHostAccess(userId);
        var operation = GetOperation(userId);
        var authorization = operation.Authorization;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorization.Token);
        cancellationToken = linked.Token;
        using var lease = await _connectionGates.AcquireAsync(userId, cancellationToken).ConfigureAwait(false);
        var account = await store.UpdateAsync<StoredAccount>(userId, "account", current => (current ?? throw new InvalidOperationException("Connect Last.fm first.")) with { ScrobblingEnabled = enabled }, cancellationToken).ConfigureAwait(false);
        if (enabled && await GetAsync(userId, cancellationToken).ConfigureAwait(false) is { NeedsReconnect: false })
            operation.Publish(authorization, () => _captures[userId] = new(account.Generation, Guid.NewGuid()));
        else _captures.TryRemove(userId, out _);
    }

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
        GetOperation(userId).Cancel();
    }

    private AccountOperation GetOperation(Guid userId)
    {
        RequireUser(userId);
        if (_operations.TryGetValue(userId, out var operation)) return operation;
        lock (_operationSync)
        {
            if (_operations.TryGetValue(userId, out operation)) return operation;
            if (_operations.Count >= 8192) throw new InvalidOperationException("Too many connected accounts.");
            operation = new AccountOperation();
            _operations[userId] = operation;
            return operation;
        }
    }

    public void Dispose()
    {
        foreach (var source in _operations.Values) { source.Cancel(); source.Dispose(); }
        _operations.Clear();
        _captures.Clear();
        _hostSuspended.Clear();
        _connectionGates.Dispose();
    }

    public sealed record StoredAccount(string Username, string ProtectedSession, bool NeedsReconnect, bool ScrobblingEnabled, Guid Generation = default, string? ApplicationIdentity = null);

    public sealed record StoredAttempt(Guid Id, string ProtectedToken, DateTimeOffset ExpiresAt, string ApplicationIdentity = "", Guid Revision = default);
}
