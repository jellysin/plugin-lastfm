using System.Reflection;
using System.Text.Json;
using JellySin.Plugin.Lastfm.Api;
using MediaBrowser.Controller;
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
        var controller = Create();
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
        var controller = Create();
        var file = Assert.IsType<FileStreamResult>(controller.Asset(asset));
        await using var stream = file.FileStream;
        Assert.True(stream.Length > 0);
        Assert.Equal(contentType, file.ContentType);
        Assert.Contains("script-src 'self'", controller.Response.Headers.ContentSecurityPolicy.ToString(), StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'self'", controller.Response.Headers.ContentSecurityPolicy.ToString(), StringComparison.Ordinal);
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"]);
    }

    [Fact]
    public void EveryEmbeddedScriptModuleIsServed()
    {
        // The dashboard imports every compiled module; one missing from the allow-list breaks the whole page.
        const string prefix = "JellySin.Plugin.Lastfm.Web.";
        var scripts = typeof(PublicController).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".js", StringComparison.Ordinal))
            .Select(name => name[prefix.Length..])
            .ToArray();
        Assert.Contains("app.js", scripts);
        var controller = Create();
        Assert.All(scripts, script => Assert.IsType<FileStreamResult>(controller.Asset(script)));
    }

    [Theory]
    [InlineData("../app.js")]
    [InlineData("admin.html")]
    [InlineData("private.json")]
    public void UnknownResourcesCannotEscapeAllowList(string asset)
    {
        var controller = Create();
        Assert.IsType<NotFoundResult>(controller.Asset(asset));
    }

    [Fact]
    public async Task CanonicalPageServesEmbeddedHtml()
    {
        var controller = Create();
        controller.Request.Path = "/JellySin/Lastfm/";
        var page = Assert.IsType<FileStreamResult>(controller.Page());
        await using var stream = page.FileStream;
        Assert.Equal("text/html; charset=utf-8", page.ContentType);
        Assert.True(stream.Length > 0);
    }

    [Fact]
    public void PublicControllerExposesNoPostAction()
    {
        var postMethods = typeof(PublicController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.GetCustomAttributes(typeof(HttpPostAttribute), inherit: true).Length > 0)
            .ToArray();
        Assert.Empty(postMethods);
    }

    private static PublicController Create()
    {
        var host = new Mock<IServerApplicationHost>();
        host.SetupGet(value => value.SystemId).Returns("test-server");
        var controller = new PublicController(host.Object)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.Request.Scheme = "https";
        controller.Request.Host = new HostString("jellyfin.example");
        return controller;
    }
}
