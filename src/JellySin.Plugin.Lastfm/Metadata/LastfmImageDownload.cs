using System.Net;
using System.Net.Http.Headers;

namespace JellySin.Plugin.Lastfm.Metadata;

/// <summary>Fetches one Last.fm image within a deadline and a size cap, and hands Jellyfin the bytes it read.</summary>
internal static class LastfmImageDownload
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private const int ChunkBytes = 81_920;

    /// <summary>
    /// A 404 for an answer the plugin refuses (address after a redirect, content type, size); the status of any other
    /// unsuccessful answer; a response holding the whole body otherwise. An overrun deadline raises an
    /// <see cref="HttpRequestException"/>, which Jellyfin's image download treats as a failed request.
    /// </summary>
    public static async Task<HttpResponseMessage> FetchAsync(HttpClient http, Uri uri, TimeProvider time, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(Deadline, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            return await ReadAsync(http, uri, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException("The Last.fm image request exceeded its deadline.", ex);
        }
    }

    private static async Task<HttpResponseMessage> ReadAsync(HttpClient http, Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        // The host's client follows redirects; an answer that came from anywhere else is not used.
        if (response.RequestMessage?.RequestUri is { } final && LastfmImages.Fetchable(final.AbsoluteUri) is null) return Refused();
        if (!response.IsSuccessStatusCode) return new HttpResponseMessage(response.StatusCode);

        var type = response.Content.Headers.ContentType?.MediaType;
        if (type is null || !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return Refused();
        if (response.Content.Headers.ContentLength > MaxBytes) return Refused();

        var body = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        if (body is null || body.Length == 0) return Refused();
        var result = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        result.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return result;
    }

    // The body, or null when it is longer than the cap; a body without a length is read only up to the cap.
    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[ChunkBytes];
            while (buffer.Length <= MaxBytes)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0) return buffer.ToArray();
                buffer.Write(chunk, 0, read);
            }

            return null;
        }
    }

    private static HttpResponseMessage Refused() => new(HttpStatusCode.NotFound);
}
