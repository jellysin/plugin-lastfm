using JellySin.Plugin.Lastfm.Api;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Storage;
using JellySin.Plugin.Lastfm.Transport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace JellySin.Plugin.Lastfm.Tests.Api;

public sealed class ExceptionRedactionTests
{
    [Theory]
    [InlineData("permission", 403)]
    [InlineData("argument", 400)]
    [InlineData("missing", 404)]
    [InlineData("conflict", 409)]
    [InlineData("not connected", 409)]
    [InlineData("remote", 502)]
    [InlineData("cancelled", 408)]
    [InlineData("storage", 503)]
    [InlineData("budget", 507)]
    [InlineData("unknown", 500)]
    public void ExceptionResponsesAndLogsNeverExposeExceptionMessage(string category, int expectedStatus)
    {
        const string secret = "SECRET-CREDENTIAL-NEVER-LOG";
        Exception exception = category switch
        {
            "permission" => new UnauthorizedAccessException(secret),
            "argument" => new ArgumentException(secret),
            "missing" => new KeyNotFoundException(secret),
            "conflict" => new InvalidOperationException(secret),
            "not connected" => new NotConnectedException(),
            "remote" => new LastfmException(9),
            "cancelled" => new OperationCanceledException(secret),
            "storage" => new IOException(secret),
            "budget" => new StorageBudgetException(),
            _ => new Exception(secret)
        };
        var context = new ExceptionContext(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), []) { Exception = exception };
        var logger = new FakeLogger<ApiExceptionFilter>();
        new ApiExceptionFilter(logger).OnException(context);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(expectedStatus, result.StatusCode);
        Assert.True(context.ExceptionHandled);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        if (category == "not connected") Assert.Equal(ApiExceptionFilter.NotConnectedType, problem.Type);
        else if (category == "conflict") Assert.Null(problem.Type);
        Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, Output(logger), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PATCH", "another method")]
    [InlineData("GET", "GET")]
    public void FailureLineNamesTheDeclaredRouteAndAListedMethodOnly(string method, string expectedMethodLog)
    {
        const string template = "JellySin/Lastfm/Me/Listening";
        const string marker = "PLANTED-LEAK-MARKER";
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = method;
        httpContext.Request.Path = $"/{marker}/path";
        httpContext.Request.QueryString = new QueryString($"?param={marker}");
        var actionDescriptor = new ActionDescriptor
        {
            AttributeRouteInfo = new AttributeRouteInfo
            {
                Template = template,
            },
        };
        var context = new ExceptionContext(new ActionContext(httpContext, new RouteData(), actionDescriptor), [])
        {
            Exception = new InvalidOperationException("boom"),
        };
        var logger = new FakeLogger<ApiExceptionFilter>();
        new ApiExceptionFilter(logger).OnException(context);
        var output = Output(logger);
        Assert.Contains(template, output, StringComparison.Ordinal);
        Assert.Contains(expectedMethodLog, output, StringComparison.Ordinal);
        Assert.DoesNotContain(marker, output, StringComparison.Ordinal);
    }

    private static string Output(FakeLogger<ApiExceptionFilter> logger)
    {
        var records = logger.Collector.GetSnapshot();
        Assert.All(records, record => Assert.Null(record.Exception));
        return string.Concat(records.Select(record => record.Message));
    }
}
