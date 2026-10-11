using System.ComponentModel.DataAnnotations;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Transport;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Api;

[Route("JellySin/Lastfm/Me")]
public sealed class AccountController(AccountService accounts, ServerAddress server, ILogger<AccountController> logger) : PrivateController
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
        var page = request.ReturnToSettings ? "the settings page" : "the music dashboard";
        ConnectionAttempt attempt;
        try
        {
            attempt = await accounts.BeginAsync(CallerId, ConnectionCallback.Address(Request, server), request.ReturnToSettings, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            logger.LogInformation("Last.fm sign-in not begun for user {User} from {Page}: {Refusal}. The request reached this server over {Scheme}; {PageAddress}.",
                LogName.Of(CallerId), page, BeginRefusal(error), Request.Scheme, server.PageAddress(Request));
            throw;
        }

        // The binding goes to the browser only, out of reach of page script; the response names the Last.fm address alone.
        ConnectionCallback.Bind(HttpContext, server, attempt.BrowserBinding);
        logger.LogInformation("Last.fm sign-in begun for user {User} from {Page}. The return address uses {ReturnScheme}; the request reached this server over {Scheme}; {PageAddress}.",
            LogName.Of(CallerId), page, server.IsHttps(Request) ? "https" : "http", Request.Scheme, server.PageAddress(Request));
        return new ConnectionStart(attempt.AuthorizationUrl, attempt.ExpiresAt);
    }

    /// <summary>
    /// Takes note that the page did not open Last.fm although the server answered: the page decides that in the browser,
    /// and without this line nothing in the server log would show the click.
    /// </summary>
    [HttpPost("Connection/Refusal")]
    public IActionResult Refusal(ConnectionRefusal refusal)
    {
        if (!ConnectionRefusal.Reasons.TryGetValue(refusal.Reason, out var reason)) return BadRequest();
        logger.LogInformation("The page did not open Last.fm for user {User}: {Refusal}. The request reached this server over {Scheme}; {PageAddress}.",
            LogName.Of(CallerId), reason, Request.Scheme, server.PageAddress(Request));
        return NoContent();
    }

    [HttpPost("Connection/Finish")]
    public async Task<AccountStatus> Finish(CancellationToken cancellationToken)
    {
        var status = await accounts.FinishAsync(CallerId, cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Last.fm sign-in finished for user {User}: {Outcome}.", LogName.Of(CallerId), status.Connected ? "connected" : "not connected");
        return status;
    }

    private static string BeginRefusal(Exception error) => error switch
    {
        ArgumentException => "the server has no usable return address",
        LastfmException { Code: 10 } => "no Last.fm application is available",
        UnauthorizedAccessException => "the account may not use the plugin",
        OperationCanceledException => "the request was cancelled",
        _ => "the attempt could not be stored",
    };

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
/// <summary>Why the page did not open Last.fm. The page sends a name from a fixed set; the log gets the phrase, never text from the browser.</summary>
public sealed record ConnectionRefusal([Required, StringLength(32)] string Reason)
{
    public static readonly IReadOnlyDictionary<string, string> Reasons = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["return-address"] = "the return address the server names is not at the address of the page",
        ["approval-address"] = "the server named no address of the Last.fm approval page",
    };
}
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
