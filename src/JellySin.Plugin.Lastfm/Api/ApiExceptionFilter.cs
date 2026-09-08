using JellySin.Plugin.Lastfm.Storage;
using JellySin.Plugin.Lastfm.Transport;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Api;

public sealed class ApiExceptionFilter(ILogger<ApiExceptionFilter> logger) : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        var (status, title) = context.Exception switch
        {
            UnauthorizedAccessException => (403, "This action is not permitted."),
            ArgumentException => (400, "Check the supplied values."),
            KeyNotFoundException => (404, "This item is no longer available."),
            InvalidOperationException => (409, "The operation is unavailable or its preview has expired. Refresh and try again."),
            LastfmException => (502, "Last.fm could not complete the request. Check your connection and try again."),
            OperationCanceledException => (408, "The operation was cancelled or timed out."),
            StorageBudgetException => (507, "The Last.fm data storage limit was reached. Any saved progress is retained."),
            IOException => (503, "Plugin storage is temporarily unavailable or full."),
            _ => (500, "The operation could not be completed."),
        };
        // Exception text can contain authenticated transport details; log only the exception type.
        logger.LogWarning("Plugin request failed with {ErrorType} and HTTP {Status}", context.Exception.GetType().Name, status);
        context.Result = new ObjectResult(new ProblemDetails { Status = status, Title = title }) { StatusCode = status };
        context.ExceptionHandled = true;
    }
}
