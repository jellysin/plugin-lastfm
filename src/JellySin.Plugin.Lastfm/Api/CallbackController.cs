using JellySin.Plugin.Lastfm.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Api;

/// <summary>
/// Where Last.fm sends the browser after the listener allowed access. The request carries no Jellyfin sign-in,
/// so the cookie set when the attempt began is the only link to a user. The token is parked for that user and the
/// browser is sent on at once, to an address without it; Last.fm is contacted later, from the signed-in page.
/// </summary>
[ApiController]
[AllowAnonymous]
[TypeFilter(typeof(ApiExceptionFilter))]
[Route(ConnectionCallback.Route)]
public sealed class CallbackController(AccountService accounts, ServerAddress server, ReturnLogLimiter limiter, ILogger<CallbackController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Approve([FromQuery] string? attempt, [FromQuery] string? token, CancellationToken cancellationToken)
    {
        ResponseSecurity.Apply(Response);
        var approval = await accounts.ApproveAsync(ConnectionCallback.Binding(Request, server), attempt, token, cancellationToken).ConfigureAwait(false);
        ConnectionCallback.Release(HttpContext, server);
        Record(approval);
        return LocalRedirect(ConnectionCallback.ReturnTarget(Request, approval));
    }

    // The line names the outcome and the check that refused; never the token, the cookie or the address of the request.
    private void Record(ConnectionApproval approval)
    {
        var page = approval.ReturnToSettings ? "the settings page" : "the music dashboard";
        if (approval.UserId is { } user)
        {
            if (approval.Accepted) logger.LogInformation("Last.fm return accepted for user {User}; the browser goes back to {Page}.", LogName.Of(user), page);
            else logger.LogInformation("Last.fm return refused for user {User}: {Refusal}. The browser goes back to {Page}.", LogName.Of(user), approval.Refusal, page);
        }
        else if (limiter.TryAcquire())
        {
            logger.LogInformation("Last.fm return refused: {Refusal}. The request reached this server over {Scheme}.", approval.Refusal, Request.Scheme);
        }
    }
}
