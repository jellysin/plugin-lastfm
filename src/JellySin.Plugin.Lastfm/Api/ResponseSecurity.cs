using Microsoft.AspNetCore.Http;

namespace JellySin.Plugin.Lastfm.Api;

/// <summary>Headers for every response served without a signed-in caller.</summary>
internal static class ResponseSecurity
{
    public static void Apply(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    }
}
