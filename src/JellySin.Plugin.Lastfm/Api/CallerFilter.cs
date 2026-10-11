using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Api;

public sealed class CallerFilter(IAuthorizationContext authorization, ServerAddress server, ILogger<CallerFilter> logger) : IAsyncActionFilter
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

        var request = context.HttpContext.Request;
        if (RequestOrigin.Refusal(request, server) is { } refusal)
        {
            // The route is the one declared in code and the rest are fixed phrases: nothing the browser sent reaches the log.
            logger.LogWarning(
                "Refused {Method} {Route}: {Refusal}. The request reached this server over {Scheme}; {PageAddress}.",
                LogName.Method(request), context.ActionDescriptor.AttributeRouteInfo?.Template ?? "an action of the plugin", refusal, LogName.Scheme(request), server.PageAddress(request));
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
    public const string OtherSite = "the browser says another site sent it";
    public const string OtherOrigin = "its origin is neither the published server address nor the address of the request";

    public static bool IsAllowed(HttpRequest request, ServerAddress server) => Refusal(request, server) is null;

    /// <summary>Why a request that changes something is not taken for one from this server's own pages; null when it is accepted.</summary>
    public static string? Refusal(HttpRequest request, ServerAddress server)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)) return null;
        var fetchSite = request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite is "cross-site" or "same-site") return OtherSite;
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return null; // Non-browser API clients use header authentication.
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) && server.Serves(request, uri) ? null : OtherOrigin;
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
