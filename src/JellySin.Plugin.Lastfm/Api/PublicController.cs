using System.Reflection;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace JellySin.Plugin.Lastfm.Api;

[ApiController]
[AllowAnonymous]
[TypeFilter(typeof(ApiExceptionFilter))]
[Route("JellySin/Lastfm")]
public sealed class PublicController(IServerApplicationHost host) : ControllerBase
{
    private static readonly HashSet<string> Assets = new(StringComparer.Ordinal)
    {
        "app.js", "client.js", "embedded.js", "account.js", "music.js", "favourites.js", "playlists.js", "rules.js", "view.js", "catalogs.js", "i18n.js", "style.css", "entry.js",
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

    private IActionResult Resource(string file, string contentType)
    {
        SecurityHeaders();
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"JellySin.Plugin.Lastfm.Web.{file}");
        return stream is null ? NotFound() : File(stream, contentType);
    }

    private void SecurityHeaders() => ResponseSecurity.Apply(Response);
}
