using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;

namespace JellySin.Plugin.Lastfm.Api;

/// <summary>
/// How the log names what a request brings: fixed words chosen by the server, never the request's own text, which could
/// carry a line break and with it a line of its own.
/// </summary>
internal static class LogName
{
    private static readonly string[] Methods = ["POST", "PUT", "DELETE"];

    /// <summary>The same short value for the same account, from which the account cannot be read.</summary>
    public static string Of(Guid userId) => Convert.ToHexStringLower(SHA256.HashData(userId.ToByteArray()))[..12];

    /// <summary>The method of a write the plugin declares, or a phrase that reads the same way before a route.</summary>
    public static string Method(HttpRequest request)
        => Array.Find(Methods, method => HttpMethods.Equals(method, request.Method)) ?? "a request to";

    /// <summary>The scheme over which the request reached this server.</summary>
    public static string Scheme(HttpRequest request)
    {
        if (string.Equals(request.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return Uri.UriSchemeHttps;
        return string.Equals(request.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ? Uri.UriSchemeHttp : "another scheme";
    }
}
