using System.Text;
using JellySin.Plugin.Lastfm.Api;
using JellySin.Plugin.Lastfm.Tests.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace JellySin.Plugin.Lastfm.Tests.Api;

public sealed class WebClientEntryTests
{
    private const string SampleHtml = "<!DOCTYPE html><html><head><title>Jellyfin</title></head><body>Hello</body></html>";
    private static readonly string ExpectedVersion = typeof(WebClientEntry).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    [Theory]
    [InlineData("/web/", "")]
    [InlineData("/web/index.html", "")]
    [InlineData("/base/web/index.html", "/base")]
    public async Task InjectsScriptWithRightPrefixAndRemovesCachingHeaders(string path, string expectedPrefix)
    {
        var (entry, _) = Create();
        var context = CreateContext("GET", path);
        context.Request.Headers["Accept-Encoding"] = "gzip, deflate";
        context.Request.Headers["If-None-Match"] = "\"12345\"";
        context.Request.Headers["If-Modified-Since"] = "Wed, 21 Oct 2026 07:28:00 GMT";

        var strippedAcceptEncoding = false;
        var strippedIfNoneMatch = false;
        var strippedIfModifiedSince = false;

        var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;

        await entry.InvokeAsync(context, ctx =>
        {
            strippedAcceptEncoding = !ctx.Request.Headers.ContainsKey("Accept-Encoding");
            strippedIfNoneMatch = !ctx.Request.Headers.ContainsKey("If-None-Match");
            strippedIfModifiedSince = !ctx.Request.Headers.ContainsKey("If-Modified-Since");

            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers.ETag = "\"etag-val\"";
            ctx.Response.Headers.LastModified = "Wed, 21 Oct 2026 07:28:00 GMT";

            var bytes = Encoding.UTF8.GetBytes(SampleHtml);
            return ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length);
        });

        Assert.True(strippedAcceptEncoding);
        Assert.True(strippedIfNoneMatch);
        Assert.True(strippedIfModifiedSince);
        Assert.False(context.Response.Headers.ContainsKey("ETag"));
        Assert.False(context.Response.Headers.ContainsKey("Last-Modified"));

        memoryStream.Position = 0;
        var body = Encoding.UTF8.GetString(memoryStream.ToArray());
        var expectedScript = $"<script type=\"module\" src=\"{expectedPrefix}/JellySin/Lastfm/Assets/entry.js?v={ExpectedVersion}\"></script></head>";
        Assert.Contains(expectedScript, body, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), context.Response.ContentLength);
    }

    [Theory]
    [InlineData("/api/items")]
    [InlineData("/web")]
    [InlineData("/other/page")]
    public async Task IgnoresOtherPaths(string path)
    {
        var (entry, _) = Create();
        var context = CreateContext("GET", path);
        context.Request.Headers["Accept-Encoding"] = "gzip";
        var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;

        await entry.InvokeAsync(context, ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(SampleHtml);
            return ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length);
        });

        Assert.True(context.Request.Headers.ContainsKey("Accept-Encoding"));
        memoryStream.Position = 0;
        var body = Encoding.UTF8.GetString(memoryStream.ToArray());
        Assert.DoesNotContain("/JellySin/Lastfm/Assets/entry.js", body, StringComparison.Ordinal);
        Assert.Equal(SampleHtml, body);
    }

    [Fact]
    public async Task IgnoresPostMethod()
    {
        var (entry, _) = Create();
        var context = CreateContext("POST", "/web/index.html");
        var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;

        await entry.InvokeAsync(context, ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(SampleHtml);
            return ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length);
        });

        memoryStream.Position = 0;
        var body = Encoding.UTF8.GetString(memoryStream.ToArray());
        Assert.DoesNotContain("/JellySin/Lastfm/Assets/entry.js", body, StringComparison.Ordinal);
        Assert.Equal(SampleHtml, body);
    }

    [Theory]
    [InlineData(304)]
    [InlineData(404)]
    public async Task IgnoresNon200Status(int statusCode)
    {
        var (entry, _) = Create();
        var context = CreateContext("GET", "/web/index.html");
        var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;

        await entry.InvokeAsync(context, ctx =>
        {
            ctx.Response.StatusCode = statusCode;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(SampleHtml);
            return ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length);
        });

        memoryStream.Position = 0;
        var body = Encoding.UTF8.GetString(memoryStream.ToArray());
        Assert.DoesNotContain("/JellySin/Lastfm/Assets/entry.js", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IgnoresNonHtmlContentType()
    {
        var (entry, _) = Create();
        var context = CreateContext("GET", "/web/index.html");
        var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;

        await entry.InvokeAsync(context, ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
            return ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length);
        });

        memoryStream.Position = 0;
        var body = Encoding.UTF8.GetString(memoryStream.ToArray());
        Assert.Equal("{\"ok\":true}", body);
    }

    [Fact]
    public async Task SkipsAlreadyInjectedPage()
    {
        var (entry, _) = Create();
        var context = CreateContext("GET", "/web/index.html");
        var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;
        var alreadyInjectedHtml = $"<html><head><script type=\"module\" src=\"/JellySin/Lastfm/Assets/entry.js?v={ExpectedVersion}\"></script></head><body></body></html>";

        await entry.InvokeAsync(context, ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(alreadyInjectedHtml);
            return ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length);
        });

        memoryStream.Position = 0;
        var body = Encoding.UTF8.GetString(memoryStream.ToArray());
        Assert.Equal(alreadyInjectedHtml, body);
    }

    [Theory]
    [InlineData("/<script>/web/index.html")]
    [InlineData("/\"/web/index.html")]
    public async Task RejectsPrefixWithSpecialCharacters(string path)
    {
        var (entry, _) = Create();
        var context = CreateContext("GET", path);
        var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;

        await entry.InvokeAsync(context, ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(SampleHtml);
            return ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length);
        });

        memoryStream.Position = 0;
        var body = Encoding.UTF8.GetString(memoryStream.ToArray());
        Assert.DoesNotContain("/JellySin/Lastfm/Assets/entry.js", body, StringComparison.Ordinal);
        Assert.Equal(SampleHtml, body);
    }

    [Fact]
    public async Task InjectsWhenBodyIsExactlyAtCap()
    {
        var (entry, _) = Create();
        var context = CreateContext("GET", "/web/index.html");
        var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;

        const int cap = 1024 * 1024;
        const string prefixPart = "<html><head>";
        const string suffixPart = "</head><body></body></html>";
        var paddingLength = cap - Encoding.UTF8.GetByteCount(prefixPart) - Encoding.UTF8.GetByteCount(suffixPart);
        var html = prefixPart + new string('a', paddingLength) + suffixPart;
        Assert.Equal(cap, Encoding.UTF8.GetByteCount(html));

        await entry.InvokeAsync(context, ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(html);
            return ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length);
        });

        memoryStream.Position = 0;
        var body = Encoding.UTF8.GetString(memoryStream.ToArray());
        Assert.Contains("/JellySin/Lastfm/Assets/entry.js", body, StringComparison.Ordinal);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), context.Response.ContentLength);
    }

    [Fact]
    public async Task OverCapPassesThroughUnmodifiedBytesAndLogsOnce()
    {
        var (entry, logger) = Create();
        var context = CreateContext("GET", "/web/index.html");
        var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;

        const int capPlusOne = 1024 * 1024 + 1;
        const string prefixPart = "<html><head>";
        const string suffixPart = "</head><body></body></html>";
        var paddingLength = capPlusOne - Encoding.UTF8.GetByteCount(prefixPart) - Encoding.UTF8.GetByteCount(suffixPart);
        var html = prefixPart + new string('b', paddingLength) + suffixPart;
        Assert.Equal(capPlusOne, Encoding.UTF8.GetByteCount(html));

        await entry.InvokeAsync(context, ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(html);
            return ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length);
        });

        memoryStream.Position = 0;
        var body = Encoding.UTF8.GetString(memoryStream.ToArray());
        Assert.DoesNotContain("/JellySin/Lastfm/Assets/entry.js", body, StringComparison.Ordinal);
        Assert.Equal(html, body);

        var secondContext = CreateContext("GET", "/web/index.html");
        var secondStream = new MemoryStream();
        secondContext.Response.Body = secondStream;

        await entry.InvokeAsync(secondContext, ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(html);
            return ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length);
        });

        var capWarnings = logger.Lines.Where(l => l.Level == LogLevel.Warning && l.Text.Contains("exceeds 1 MiB", StringComparison.Ordinal)).ToArray();
        Assert.Single(capWarnings);
    }

    [Fact]
    public async Task HeadRequestSetsContentLengthRemovesETagAndEmitsNoBody()
    {
        var (entry, _) = Create();
        var context = CreateContext("HEAD", "/web/index.html");
        var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;

        await entry.InvokeAsync(context, ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers.ETag = "\"head-etag\"";
            ctx.Response.Headers.LastModified = "Wed, 21 Oct 2026 07:28:00 GMT";
            var bytes = Encoding.UTF8.GetBytes(SampleHtml);
            return ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length);
        });

        Assert.False(context.Response.Headers.ContainsKey("ETag"));
        Assert.False(context.Response.Headers.ContainsKey("Last-Modified"));
        Assert.Equal(0, memoryStream.Length);

        var expectedScript = $"<script type=\"module\" src=\"/JellySin/Lastfm/Assets/entry.js?v={ExpectedVersion}\"></script>";
        var expectedLength = Encoding.UTF8.GetByteCount(SampleHtml) + Encoding.UTF8.GetByteCount(expectedScript);
        Assert.Equal(expectedLength, context.Response.ContentLength);
    }

    [Fact]
    public void HostWebClientFalseReturnsNextUnchangedAndLogsWarning()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["hostwebclient"] = "false"
        }).Build();
        var logger = new FakeLogger<WebClientEntry>();
        var entry = new WebClientEntry(config, logger);

        Action<IApplicationBuilder> next = _ => { };

        var returned = entry.Configure(next);
        Assert.Same(next, returned);

        var warning = Assert.Single(logger.Lines);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Equal("This server does not host the Jellyfin web client, so the Last.fm entry in user settings is unavailable. Scrobbling continues.", warning.Text);
    }

    [Fact]
    public async Task MissingHeadElementLeavesResponseUnmodifiedAndLogsNamedWarningOnce()
    {
        var (entry, logger) = Create();
        var context = CreateContext("GET", "/web/index.html");
        var memoryStream = new MemoryStream();
        context.Response.Body = memoryStream;
        const string noHeadHtml = "<html><body>No head element here</body></html>";

        await entry.InvokeAsync(context, ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(noHeadHtml);
            return ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length);
        });

        memoryStream.Position = 0;
        var body = Encoding.UTF8.GetString(memoryStream.ToArray());
        Assert.Equal(noHeadHtml, body);

        var secondContext = CreateContext("GET", "/web/index.html");
        var secondStream = new MemoryStream();
        secondContext.Response.Body = secondStream;

        await entry.InvokeAsync(secondContext, ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/html; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(noHeadHtml);
            return ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length);
        });

        var headWarnings = logger.Lines.Where(l => l.Level == LogLevel.Warning && l.Text.Contains("no head element", StringComparison.Ordinal)).ToArray();
        Assert.Single(headWarnings);
        Assert.Equal("The Jellyfin web page has no head element, so the Last.fm entry in user settings is unavailable. Scrobbling continues.", headWarnings[0].Text);
    }

    private static (WebClientEntry Entry, FakeLogger<WebClientEntry> Logger) Create()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["hostwebclient"] = "true"
        }).Build();
        var logger = new FakeLogger<WebClientEntry>();
        var entry = new WebClientEntry(config, logger);
        return (entry, logger);
    }

    private static DefaultHttpContext CreateContext(string method, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        return context;
    }
}
