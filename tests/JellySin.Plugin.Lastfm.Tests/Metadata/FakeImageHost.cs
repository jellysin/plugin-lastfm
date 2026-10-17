using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace JellySin.Plugin.Lastfm.Tests.Metadata;

/// <summary>The host's HTTP client factory and the handler behind it, both fake: it records every request and answers as told.</summary>
internal sealed class FakeImageHost : HttpMessageHandler, IHttpClientFactory
{
    public const string Star = "2a96cbd8b46e442fc41c2b86b821562f";
    public const string Disc = "c6f59c1e5e7240a4c0d427abd71f3dbb";
    public const string Real = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = (_, _) => Task.FromResult(Image(new byte[] { 1, 2, 3 }));
    public List<Uri> Requests { get; } = [];
    public List<string> ClientNames { get; } = [];

    public HttpClient CreateClient(string name)
    {
        ClientNames.Add(name);
        return new HttpClient(this, false);
    }

    public static string Url(string hash, string size = "300x300", string extension = "png") =>
        $"https://lastfm.freetls.fastly.net/i/u/{size}/{hash}.{extension}";

    public static HttpResponseMessage Image(byte[] body, string type = "image/png") =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) { Headers = { ContentType = new MediaTypeHeaderValue(type) } } };

    /// <summary>The answer of Last.fm's getInfo with the listed images, in the order given.</summary>
    public static JsonDocument Info(string node, params (string Size, string Url)[] images) => JsonDocument.Parse(JsonSerializer.Serialize(
        new Dictionary<string, object>
        {
            [node] = new Dictionary<string, object>
            {
                ["name"] = "Cher",
                ["image"] = images.Select(image => new Dictionary<string, string> { ["#text"] = image.Url, ["size"] = image.Size }).ToArray(),
            },
        }));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        var response = await Respond(request, cancellationToken).ConfigureAwait(false);
        response.RequestMessage ??= request;
        return response;
    }
}

/// <summary>A body that never ends and counts what was read from it.</summary>
internal sealed class EndlessStream : Stream
{
    public long BytesRead { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        Array.Fill(buffer, (byte)7, offset, count);
        BytesRead += count;
        return count;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
