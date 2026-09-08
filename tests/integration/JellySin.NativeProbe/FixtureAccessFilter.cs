using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace JellySin.NativeProbe;

public sealed class FixtureAccessFilter(IAuthorizationContext authorization) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var caller = await authorization.GetAuthorizationInfo(context.HttpContext).ConfigureAwait(false);
        if (Environment.GetEnvironmentVariable("JELLYSIN_NATIVE_PROBE") != "1"
            || !caller.IsAuthenticated || caller.IsApiKey || caller.User is null
            || caller.User.HasPermission(PermissionKind.IsDisabled)
            || !caller.User.HasPermission(PermissionKind.IsAdministrator))
        {
            context.Result = new ForbidResult();
            return;
        }

        context.HttpContext.Response.Headers.CacheControl = "no-store";
        await next().ConfigureAwait(false);
    }
}
