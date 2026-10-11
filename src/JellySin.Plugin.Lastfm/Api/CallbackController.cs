using JellySin.Plugin.Lastfm.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
public sealed class CallbackController(AccountService accounts) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Approve([FromQuery] string? attempt, [FromQuery] string? token, CancellationToken cancellationToken)
    {
        ResponseSecurity.Apply(Response);
        var approval = await accounts.ApproveAsync(ConnectionCallback.Binding(Request), attempt, token, cancellationToken).ConfigureAwait(false);
        ConnectionCallback.Release(HttpContext);
        return LocalRedirect(ConnectionCallback.ReturnTarget(Request, approval));
    }
}
