using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Storage;
using JellySin.Plugin.Lastfm.Transport;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Api;

public sealed class ApiExceptionFilter(ILogger<ApiExceptionFilter> logger) : IExceptionFilter
{
    /// <summary>Marks the one conflict the page explains differently: no Last.fm account is connected. web/client.ts holds the same value.</summary>
    public const string NotConnectedType = "urn:jellysin:lastfm:not-connected";

    public void OnException(ExceptionContext context)
    {
        var (status, title, type) = context.Exception switch
        {
            NotConnectedException => (409, "Connect Last.fm first.", NotConnectedType),
            UnauthorizedAccessException => (403, "This action is not permitted.", null),
            ArgumentException => (400, "Check the supplied values.", null),
            KeyNotFoundException => (404, "This item is no longer available.", null),
            InvalidOperationException => (409, "The operation is unavailable or its preview has expired. Refresh and try again.", null),
            LastfmException => (502, "Last.fm could not complete the request. Check your connection and try again.", null),
            OperationCanceledException => (408, "The operation was cancelled or timed out.", null),
            StorageBudgetException => (507, "The Last.fm data storage limit was reached. Any saved progress is retained.", null),
            IOException => (503, "Plugin storage is temporarily unavailable or full.", null),
            _ => (500, "The operation could not be completed.", null),
        };
        // Exception text can contain authenticated transport details; log only the exception type.
        // The route is the template declared in code, never the address the caller sent.
        logger.LogWarning(
            "Plugin request {Method} {Route} failed with {ErrorType} and HTTP {Status}",
            LogName.RequestMethod(context.HttpContext.Request),
            context.ActionDescriptor.AttributeRouteInfo?.Template ?? "an undeclared route",
            context.Exception.GetType().Name,
            status);
        context.Result = new ObjectResult(new ProblemDetails { Status = status, Title = title, Type = type }) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}
