using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace JellySin.Plugin.Lastfm.Api;

public sealed class CallerFilter(IAuthorizationContext authorization) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var identity = await authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false);
        if (!identity.IsAuthenticated || identity.IsApiKey || identity.UserId == Guid.Empty
            || identity.User is null || identity.User.HasPermission(PermissionKind.IsDisabled))
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        if (!RequestOrigin.IsAllowed(context.HttpContext.Request))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
            return;
        }

        context.HttpContext.Items[typeof(CallerFilter)] = identity;
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        await next().ConfigureAwait(false);
    }
}

internal static class RequestOrigin
{
    public static bool IsAllowed(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) return true;
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite is "cross-site" or "same-site") return false;
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return true; // Non-browser API clients use header authentication.
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase);
    }
}

[ApiController]
[Microsoft.AspNetCore.Authorization.Authorize]
[TypeFilter(typeof(CallerFilter))]
[TypeFilter(typeof(ApiExceptionFilter))]
[RequestSizeLimit(16_384)]
public abstract class PrivateController : ControllerBase
{
    protected AuthorizationInfo Identity => (AuthorizationInfo)HttpContext.Items[typeof(CallerFilter)]!;
    protected Guid CallerId => Identity.UserId;
}
