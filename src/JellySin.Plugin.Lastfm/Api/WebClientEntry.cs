using System.Reflection;
using System.Text;
using MediaBrowser.Controller.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Api;

/// <summary>
/// Wraps the HTTP pipeline to inject the Last.fm entry script into served Jellyfin web client pages.
/// </summary>
public sealed class WebClientEntry(IConfiguration configuration, ILogger<WebClientEntry> logger) : IStartupFilter
{
    private const int MaxBodySize = 1024 * 1024;
    private const string ScriptPath = "/JellySin/Lastfm/Assets/entry.js";
    private static readonly string Version = typeof(WebClientEntry).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private int _loggedHeadWarning;
    private int _loggedCapWarning;

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        if (!configuration.HostWebClient())
        {
            logger.LogWarning("This server does not host the Jellyfin web client, so the Last.fm entry in user settings is unavailable. Scrobbling continues.");
            return next;
        }

        return app =>
        {
            app.Use(InvokeAsync);
            next(app);
        };
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var path = context.Request.Path.Value ?? string.Empty;
        if (!TryGetPrefix(path, out var prefix))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");

        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        await ProcessResponseAsync(context, buffer, originalBody, prefix).ConfigureAwait(false);
    }

    private async Task ProcessResponseAsync(HttpContext context, MemoryStream buffer, Stream originalBody, string prefix)
    {
        var isHtml = context.Response.StatusCode == StatusCodes.Status200OK
            && context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true;

        if (!isHtml)
        {
            await FlushUnmodifiedAsync(context, buffer, originalBody).ConfigureAwait(false);
            return;
        }

        var bytes = buffer.ToArray();
        if (!TryInject(bytes, prefix, out var output))
        {
            await FlushUnmodifiedAsync(context, buffer, originalBody).ConfigureAwait(false);
            return;
        }

        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.ContentLength = output.Length;

        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await originalBody.WriteAsync(output, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private bool TryInject(byte[] input, string prefix, out byte[] output)
    {
        output = input;
        if (input.Length > MaxBodySize)
        {
            if (Interlocked.Exchange(ref _loggedCapWarning, 1) == 0)
            {
                logger.LogWarning("The Jellyfin web page exceeds 1 MiB, so the Last.fm entry in user settings is unavailable. Scrobbling continues.");
            }
            return false;
        }

        var html = Encoding.UTF8.GetString(input);
        if (html.Contains(ScriptPath, StringComparison.Ordinal))
        {
            return false;
        }

        var headIndex = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        if (headIndex < 0)
        {
            if (Interlocked.Exchange(ref _loggedHeadWarning, 1) == 0)
            {
                logger.LogWarning("The Jellyfin web page has no head element, so the Last.fm entry in user settings is unavailable. Scrobbling continues.");
            }
            return false;
        }

        var scriptTag = $"<script type=\"module\" src=\"{prefix}{ScriptPath}?v={Version}\"></script>";
        var modified = string.Concat(html.AsSpan(0, headIndex), scriptTag, html.AsSpan(headIndex));
        output = Encoding.UTF8.GetBytes(modified);
        return true;
    }

    private static async Task FlushUnmodifiedAsync(HttpContext context, MemoryStream buffer, Stream originalBody)
    {
        if (!HttpMethods.IsHead(context.Request.Method) && buffer.Length > 0)
        {
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static bool TryGetPrefix(string path, out string prefix)
    {
        prefix = string.Empty;
        if (path.EndsWith("/web/", StringComparison.Ordinal))
        {
            prefix = path[..^"/web/".Length];
            return IsValidPrefix(prefix);
        }

        if (path.EndsWith("/web/index.html", StringComparison.Ordinal))
        {
            prefix = path[..^"/web/index.html".Length];
            return IsValidPrefix(prefix);
        }

        return false;
    }

    private static bool IsValidPrefix(string prefix)
    {
        foreach (var ch in prefix)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch is not ('/' or '_' or '-' or '.' or '~' or '%'))
            {
                return false;
            }
        }

        return true;
    }
}
