using JellySin.Plugin.Lastfm.Configuration;
using Microsoft.AspNetCore.Http;

namespace JellySin.Plugin.Lastfm.Api;

/// <summary>The return address for a Last.fm sign-in and the cookie that ties an attempt to the browser that began it.</summary>
internal static class ConnectionCallback
{
    public const string Route = "JellySin/Lastfm/Callback";
    private const string SettingsPage = "/web/index.html#/configurationpage?name=jellysin-lastfm";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    /// <summary>The address Last.fm sends the browser back to, as this request reached the server.</summary>
    public static Uri Address(HttpRequest request)
    {
        // A request without a host yields no absolute address. The account service refuses any scheme but HTTP and HTTPS.
        if (!Uri.TryCreate($"{request.Scheme}://{request.Host.Value}{request.PathBase}/{Route}", UriKind.Absolute, out var address))
            throw new ArgumentException("The server address of this request cannot be used for the return from Last.fm.", nameof(request));
        return address;
    }

    // On HTTPS the name carries the host-only prefix, so no other host or plain-HTTP page can set it.
    public static string CookieName(HttpRequest request) => request.IsHttps ? "__Host-JellySinLastfmAttempt" : "JellySinLastfmAttempt";

    public static void Bind(HttpContext context, string binding)
        => context.Response.Cookies.Append(CookieName(context.Request), binding, Options(context.Request, Lifetime));

    public static string? Binding(HttpRequest request) => request.Cookies[CookieName(request)];

    public static void Release(HttpContext context)
        => context.Response.Cookies.Delete(CookieName(context.Request), Options(context.Request, null));

    /// <summary>The page that began the attempt; the token from Last.fm is never part of this address.</summary>
    public static string ReturnTarget(HttpRequest request, ConnectionApproval approval)
    {
        if (approval.ReturnToSettings) return $"{request.PathBase}{SettingsPage}{(approval.Accepted ? string.Empty : "&connection=failed")}";
        return $"{request.PathBase}/JellySin/Lastfm/{(approval.Accepted ? string.Empty : "?connection=failed")}";
    }

    private static CookieOptions Options(HttpRequest request, TimeSpan? lifetime) => new()
    {
        HttpOnly = true,
        Secure = request.IsHttps,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        Path = request.IsHttps ? "/" : $"{request.PathBase}/{Route}",
        MaxAge = lifetime,
    };
}
