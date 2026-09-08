using System.Text.Json;
using JellySin.Plugin.Lastfm.Api;
using JellySin.Plugin.Lastfm.Tests.Core;
using MediaBrowser.Controller;
using MediaBrowser.Controller.QuickConnect;
using MediaBrowser.Model.QuickConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Api;

public sealed class PublicSurfaceTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/jellyfin")]
    public void BootstrapAndRedirectPreserveReverseProxyBasePath(string prefix)
    {
        var (controller, _) = Create();
        controller.Request.PathBase = prefix;
        controller.Request.Path = "/JellySin/Lastfm";
        var redirect = Assert.IsType<LocalRedirectResult>(controller.Page());
        Assert.Equal(prefix + "/JellySin/Lastfm/", redirect.Url);
        var bootstrap = JsonSerializer.SerializeToElement(controller.Bootstrap());
        Assert.Equal(prefix, bootstrap.GetProperty("BasePath").GetString());
        Assert.Equal("test-server", bootstrap.GetProperty("ServerId").GetString());
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
        Assert.Equal("no-referrer", controller.Response.Headers["Referrer-Policy"]);
    }

    [Theory]
    [InlineData("app.js", "text/javascript; charset=utf-8")]
    [InlineData("style.css", "text/css; charset=utf-8")]
    public async Task EmbeddedAssetsHaveFixedContentTypesAndStrictSecurityHeaders(string asset, string contentType)
    {
        var (controller, _) = Create();
        var file = Assert.IsType<FileStreamResult>(controller.Asset(asset));
        await using var stream = file.FileStream;
        Assert.True(stream.Length > 0);
        Assert.Equal(contentType, file.ContentType);
        Assert.Contains("script-src 'self'", controller.Response.Headers.ContentSecurityPolicy.ToString(), StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", controller.Response.Headers.ContentSecurityPolicy.ToString(), StringComparison.Ordinal);
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"]);
    }

    [Theory]
    [InlineData("../app.js")]
    [InlineData("admin.html")]
    [InlineData("private.json")]
    public void UnknownResourcesCannotEscapeAllowList(string asset)
    {
        var (controller, _) = Create();
        Assert.IsType<NotFoundResult>(controller.Asset(asset));
    }

    [Fact]
    public async Task CanonicalPageServesEmbeddedHtml()
    {
        var (controller, _) = Create();
        controller.Request.Path = "/JellySin/Lastfm/";
        var page = Assert.IsType<FileStreamResult>(controller.Page());
        await using var stream = page.FileStream;
        Assert.Equal("text/html; charset=utf-8", page.ContentType);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public void QuickConnectOnlyReturnsApprovalStateAndKeepsSecretOutOfUrlAndResult()
    {
        var (controller, quick) = Create();
        var secret = "a-private-quick-connect-secret";
        quick.SetupGet(value => value.IsEnabled).Returns(true);
        quick.Setup(value => value.CheckRequestStatus(secret)).Returns(new QuickConnectResult(secret, "123456", new DateTime(2026, 9, 8), "device", "browser", "JellySin", "1.0.0") { Authenticated = true });
        var input = new QuickConnectInput(secret);
        var result = Assert.IsType<OkObjectResult>(controller.QuickConnectStatus(input));
        Assert.Equal("{\"Authenticated\":true}", JsonSerializer.Serialize(result.Value));
        Assert.DoesNotContain(secret, input.ToString(), StringComparison.Ordinal);
        Assert.False(controller.Request.QueryString.HasValue);
        var action = typeof(PublicController).GetMethod(nameof(PublicController.QuickConnectStatus))!;
        Assert.Contains(action.GetCustomAttributes(false), attribute => attribute is HttpPostAttribute);
    }

    [Fact]
    public void CrossOriginQuickConnectRequestsAreRejectedBeforeHostLookup()
    {
        var (controller, quick) = Create();
        controller.Request.Headers.Origin = "https://evil.example";
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(controller.QuickConnectStatus(new QuickConnectInput("private-secret-value"))).StatusCode);
        quick.Verify(value => value.CheckRequestStatus(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void QuickConnectRateGateIsGlobalAndRecoversAfterWindow()
    {
        var clock = new TestClock();
        var (controller, _) = Create(clock);
        for (var index = 0; index < 60; index++) Assert.IsType<NotFoundResult>(controller.QuickConnectStatus(new QuickConnectInput("private-secret-value")));
        Assert.Equal(429, Assert.IsType<StatusCodeResult>(controller.QuickConnectStatus(new QuickConnectInput("private-secret-value"))).StatusCode);
        Assert.Equal("1", controller.Response.Headers.RetryAfter);
        clock.Advance(1);
        Assert.IsType<NotFoundResult>(controller.QuickConnectStatus(new QuickConnectInput("private-secret-value")));
    }

    private static (PublicController Controller, Mock<IQuickConnect> Quick) Create(TimeProvider? clock = null)
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(value => value.SystemId).Returns("test-server");
        var quick = new Mock<IQuickConnect>();
        var controller = new PublicController(host.Object, quick.Object, new QuickConnectLimiter(clock ?? TimeProvider.System))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.Request.Scheme = "https";
        controller.Request.Host = new HostString("jellyfin.example");
        controller.Request.Method = "POST";
        return (controller, quick);
    }
}
