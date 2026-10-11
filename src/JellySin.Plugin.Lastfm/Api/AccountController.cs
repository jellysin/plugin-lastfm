using System.ComponentModel.DataAnnotations;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using JellySin.Plugin.Lastfm.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace JellySin.Plugin.Lastfm.Api;

[Route("JellySin/Lastfm/Me")]
public sealed class AccountController(AccountService accounts) : PrivateController
{
    [HttpGet]
    public async Task<object> GetMe(CancellationToken cancellationToken) => new
    {
        UserName = Identity.User!.Username,
        IsAdministrator = Identity.User.HasPermission(PermissionKind.IsAdministrator),
        Connection = await accounts.GetStatusAsync(CallerId, cancellationToken).ConfigureAwait(false),
    };

    [HttpPost("Connection/Begin")]
    public async Task<ConnectionStart> Begin(ConnectionBegin request, CancellationToken cancellationToken)
    {
        var attempt = await accounts.BeginAsync(CallerId, ConnectionCallback.Address(Request), request.ReturnToSettings, cancellationToken).ConfigureAwait(false);
        // The binding goes to the browser only, out of reach of page script; the response names the Last.fm address alone.
        ConnectionCallback.Bind(HttpContext, attempt.BrowserBinding);
        return new ConnectionStart(attempt.AuthorizationUrl, attempt.ExpiresAt);
    }

    [HttpPost("Connection/Finish")]
    public Task<AccountStatus> Finish(CancellationToken cancellationToken) => accounts.FinishAsync(CallerId, cancellationToken);

    [HttpDelete("Connection")]
    public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
    {
        await accounts.DisconnectAsync(CallerId, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPut("Scrobbling")]
    public async Task<IActionResult> Scrobbling(ToggleRequest request, CancellationToken cancellationToken)
    {
        await accounts.SetScrobblingAsync(CallerId, request.Enabled, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}

public sealed record ConnectionBegin(bool ReturnToSettings = false);
public sealed record ConnectionStart(string AuthorizationUrl, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "Last.fm sign-in start (address redacted)";
}
public sealed record ToggleRequest(bool Enabled);

[Route("JellySin/Lastfm/Admin")]
public sealed class AdministrationController(ApplicationCredentialService credentials) : PrivateController
{
    [HttpGet("Application")]
    public async Task<object> Status(CancellationToken cancellationToken)
    {
        RequireAdministrator();
        var configured = await credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        var source = await credentials.GetSourceAsync(cancellationToken).ConfigureAwait(false);
        return new { Configured = configured.IsConfigured, Source = source.ToString() };
    }

    [HttpPut("Application")]
    public async Task<IActionResult> Save(ApplicationInput input, CancellationToken cancellationToken)
    {
        RequireAdministrator();
        await credentials.SetAsync(new(input.ApiKey, input.Secret), cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpDelete("Application")]
    public async Task<IActionResult> Remove(CancellationToken cancellationToken)
    {
        RequireAdministrator();
        await credentials.DeleteAsync(cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    private void RequireAdministrator()
    {
        if (!Identity.User!.HasPermission(PermissionKind.IsAdministrator)) throw new UnauthorizedAccessException();
    }
}

public sealed record ApplicationInput(
    [Required, RegularExpression("^[a-fA-F0-9]{32}$")] string ApiKey,
    [Required, RegularExpression("^[a-fA-F0-9]{32}$")] string Secret)
{
    public override string ToString() => "Application credentials (redacted)";
}
