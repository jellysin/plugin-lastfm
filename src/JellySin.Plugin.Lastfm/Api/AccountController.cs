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
    public Task<ConnectionAttempt> Begin(CancellationToken cancellationToken) => accounts.BeginAsync(CallerId, cancellationToken);

    [HttpPost("Connection/Finish")]
    public Task<AccountStatus> Finish(ConnectionFinish request, CancellationToken cancellationToken) =>
        accounts.FinishAsync(CallerId, request.AttemptId, cancellationToken);

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

public sealed record ConnectionFinish(Guid AttemptId);
public sealed record ToggleRequest(bool Enabled);

[Route("JellySin/Lastfm/Admin")]
public sealed class AdministrationController(ApplicationCredentialService credentials) : PrivateController
{
    [HttpGet("Application")]
    public async Task<object> Status(CancellationToken cancellationToken)
    {
        RequireAdministrator();
        var configured = await credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        return new { Configured = configured.IsConfigured };
    }

    [HttpPut("Application")]
    public async Task<IActionResult> Save(ApplicationInput input, CancellationToken cancellationToken)
    {
        RequireAdministrator();
        await credentials.SetAsync(new(input.ApiKey, input.Secret), cancellationToken).ConfigureAwait(false);
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
