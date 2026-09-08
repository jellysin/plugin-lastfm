using JellySin.Plugin.Lastfm.Api;
using JellySin.Plugin.Lastfm.Transport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Tests.Api;

public sealed class ExceptionRedactionTests
{
    [Theory]
    [InlineData("permission", 403)]
    [InlineData("argument", 400)]
    [InlineData("missing", 404)]
    [InlineData("conflict", 409)]
    [InlineData("remote", 502)]
    [InlineData("cancelled", 408)]
    [InlineData("storage", 503)]
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
            "remote" => new LastfmException(9),
            "cancelled" => new OperationCanceledException(secret),
            "storage" => new IOException(secret),
            _ => new Exception(secret)
        };
        var context = new ExceptionContext(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), []) { Exception = exception };
        var logger = new CapturingLogger();
        new ApiExceptionFilter(logger).OnException(context);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(expectedStatus, result.StatusCode);
        Assert.True(context.ExceptionHandled);
        Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(result.Value), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, logger.Output, StringComparison.Ordinal);
    }

    private sealed class CapturingLogger : ILogger<ApiExceptionFilter>
    {
        public string Output { get; private set; } = string.Empty;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { Output += formatter(state, exception); Assert.Null(exception); }
    }
}
