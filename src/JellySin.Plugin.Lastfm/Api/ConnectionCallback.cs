using JellySin.Plugin.Lastfm.Configuration;
using Microsoft.AspNetCore.Http;

namespace JellySin.Plugin.Lastfm.Api;

/// <summary>The return address for a Last.fm sign-in and the cookie that ties an attempt to the browser that began it.</summary>
internal static class ConnectionCallback
{
    public const string Route = "JellySin/Lastfm/Callback";
    private const string SettingsPage = "/web/index.html#/configurationpage?name=jellysin-lastfm";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>The address Last.fm sends the browser back to: the server's published address when one is set, otherwise as this request reached the server.</summary>
    public static Uri Address(HttpRequest request, ServerAddress server)
    {
        // A request without a host yields no absolute address. The account service refuses any scheme but HTTP and HTTPS.
        if (!Uri.TryCreate($"{server.Origin(request)}{request.PathBase}/{Route}", UriKind.Absolute, out var address))
            throw new ArgumentException("The server address of this request cannot be used for the return from Last.fm.", nameof(request));
        return address;
    }

    // On HTTPS the name carries the host-only prefix, so no other host or plain-HTTP page can set it. The scheme is the
    // one of the return address: behind a proxy that ends TLS the browser uses HTTPS although this request arrived over HTTP.
    public static string CookieName(HttpRequest request, ServerAddress server) => server.IsHttps(request) ? "__Host-JellySinLastfmAttempt" : "JellySinLastfmAttempt";

    public static void Bind(HttpContext context, ServerAddress server, string binding)
        => context.Response.Cookies.Append(CookieName(context.Request, server), binding, Options(context.Request, server, Lifetime));

    public static string? Binding(HttpRequest request, ServerAddress server) => request.Cookies[CookieName(request, server)];

    public static void Release(HttpContext context, ServerAddress server)
        => context.Response.Cookies.Delete(CookieName(context.Request, server), Options(context.Request, server, null));

    /// <summary>The page that began the attempt; the token from Last.fm is never part of this address.</summary>
    public static string ReturnTarget(HttpRequest request, ConnectionApproval approval)
    {
        if (approval.ReturnToSettings) return $"{request.PathBase}{SettingsPage}{(approval.Accepted ? string.Empty : "&connection=failed")}";
        return $"{request.PathBase}/JellySin/Lastfm/{(approval.Accepted ? string.Empty : "?connection=failed")}";
    }

    private static CookieOptions Options(HttpRequest request, ServerAddress server, TimeSpan? lifetime)
    {
        var encrypted = server.IsHttps(request);
        return new()
        {
            HttpOnly = true,
            Secure = encrypted,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Path = encrypted ? "/" : $"{request.PathBase}/{Route}",
            MaxAge = lifetime,
        };
    }
}
