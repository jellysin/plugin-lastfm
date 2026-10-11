using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Api;

/// <summary>
/// The scheme and host under which browsers reach this server. The address the administrator published for the
/// server decides them when one is set: a request cannot change it, and a proxy that hides the public scheme does
/// not matter. Without a published address the request's own scheme and host are used.
/// </summary>
public sealed class ServerAddress
{
    private readonly string? published;
    private readonly bool unusable;

    /// <param name="published">The server's published address as the host read it at start; empty when none is set.</param>
    /// <param name="logger">Told once when the published address cannot be used.</param>
    public ServerAddress(string? published, ILogger<ServerAddress>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(published)) return;
        // Only the scheme, host and port count: the path under which the server answers is its own base path setting.
        if (Uri.TryCreate(published.Trim(), UriKind.Absolute, out var address) && address.IdnHost.Length > 0
            && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps))
        {
            this.published = new UriBuilder(address.Scheme, address.IdnHost, address.IsDefaultPort ? -1 : address.Port).Uri.GetLeftPart(UriPartial.Authority);
            return;
        }
        unusable = true;
        // The value can carry sign-in details of a proxy, so it is not written to the log.
        logger?.LogWarning("The published server address is not an absolute HTTP or HTTPS address. Last.fm sign-in stays unavailable until the start option published-server-url is corrected.");
    }

    /// <summary>Whether browsers reach this server over HTTPS. A published address that cannot be used leaves the request's scheme.</summary>
    public bool IsHttps(HttpRequest request)
        => published is null ? request.IsHttps : published.StartsWith("https://", StringComparison.Ordinal);

    /// <summary>
    /// Whether a page with this origin is one of this server's own: at the published address, or at the address this
    /// request itself names. Behind a proxy that ends TLS the two differ in their scheme, and the browser names the first.
    /// </summary>
    public bool Serves(HttpRequest request, Uri origin)
        => string.Equals(origin.GetLeftPart(UriPartial.Authority), published, StringComparison.OrdinalIgnoreCase)
            || string.Equals(origin.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase)
            && string.Equals(origin.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>Where the page that sent this request stands, for the log: a fixed phrase, never a value the browser sent.</summary>
    public string PageAddress(HttpRequest request)
    {
        if (published is null) return unusable ? "the published server address cannot be used" : "no published server address is set";
        var named = request.Headers.Origin.ToString();
        if (named.Length == 0) return "the request names no page";
        return Uri.TryCreate(named, UriKind.Absolute, out var origin)
            && string.Equals(origin.GetLeftPart(UriPartial.Authority), published, StringComparison.OrdinalIgnoreCase)
            ? "the page is at the published server address" : "the page is not at the published server address";
    }

    /// <summary>The scheme, host and port of this server, without a trailing slash.</summary>
    public string Origin(HttpRequest request)
    {
        if (unusable) throw new ArgumentException("The published server address cannot be used for the return from Last.fm.", nameof(request));
        return published ?? $"{request.Scheme}://{request.Host.Value}";
    }
}
