using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lastfm.Providers;

internal static class LastfmMetadataClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static async Task<T?> Get<T>(IHttpClientFactory factory, ILogger logger, string url, CancellationToken cancellationToken)
    {
        using var client = factory.CreateClient();
        try
        {
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Last.fm metadata request failed with HTTP status {StatusCode}", (int)response.StatusCode);
                return default;
            }
            return await response.Content.ReadFromJsonAsync<T>(SerializerOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
        {
            logger.LogWarning("Last.fm metadata request failed ({FailureType})", exception.GetType().Name);
            return default;
        }
    }

    public static async Task<HttpResponseMessage> GetImage(IHttpClientFactory factory, string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException("An HTTP image URL is required.", nameof(url));
        }
        var secureUri = new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = uri.IsDefaultPort ? -1 : uri.Port }.Uri;
        using var client = factory.CreateClient();
        return await client.GetAsync(secureUri, cancellationToken).ConfigureAwait(false);
    }
}
