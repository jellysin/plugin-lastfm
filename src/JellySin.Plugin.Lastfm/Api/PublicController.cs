using System.ComponentModel.DataAnnotations;
using System.Reflection;
using MediaBrowser.Controller;
using MediaBrowser.Controller.QuickConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JellySin.Plugin.Lastfm.Api;

[ApiController]
[AllowAnonymous]
[TypeFilter(typeof(ApiExceptionFilter))]
[Route("JellySin/Lastfm")]
public sealed class PublicController(IServerApplicationHost host, IQuickConnect quickConnect, QuickConnectLimiter limiter) : ControllerBase
{
    private static readonly HashSet<string> Assets = new(StringComparer.Ordinal)
    {
        "app.js", "client.js", "auth.js", "account.js", "music.js", "favourites.js", "playlists.js", "view.js", "style.css",
    };

    [HttpGet("")]
    public IActionResult Page()
    {
        if (!Request.Path.Value!.EndsWith('/')) return LocalRedirect($"{Request.PathBase}/JellySin/Lastfm/");
        return Resource("index.html", "text/html; charset=utf-8");
    }

    [HttpGet("Assets/{file}")]
    public IActionResult Asset(string file) => Assets.Contains(file)
        ? Resource(file, file.EndsWith(".css", StringComparison.Ordinal) ? "text/css; charset=utf-8" : "text/javascript; charset=utf-8")
        : NotFound();

    [HttpGet("Bootstrap")]
    public object Bootstrap()
    {
        SecurityHeaders();
        return new
        {
            BasePath = Request.PathBase.Value ?? "",
            ServerId = host.SystemId,
            Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"
        };
    }

    [HttpPost("Auth/QuickConnect/Status")]
    [RequestSizeLimit(1024)]
    public IActionResult QuickConnectStatus(QuickConnectInput input)
    {
        SecurityHeaders();
        if (!RequestOrigin.IsAllowed(Request)) return StatusCode(403);
        if (!limiter.TryAcquire())
        {
            Response.Headers.RetryAfter = "1";
            return StatusCode(429);
        }
        if (!quickConnect.IsEnabled) return NotFound();
        return Ok(new { Authenticated = quickConnect.CheckRequestStatus(input.Secret).Authenticated });
    }

    private IActionResult Resource(string file, string contentType)
    {
        SecurityHeaders();
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"JellySin.Plugin.Lastfm.Web.{file}");
        return stream is null ? NotFound() : File(stream, contentType);
    }

    private void SecurityHeaders()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    }
}

public sealed record QuickConnectInput([Required, StringLength(128, MinimumLength = 16)] string Secret)
{
    public override string ToString() => "Quick Connect request (redacted)";
}
