using System.Net;
using System.Text;

namespace Jellyfin.Plugin.Lastfm.Tests;

internal sealed class TestHttpClientFactory : IHttpClientFactory, IDisposable
{
    private readonly DelegateHandler _handler;
    private readonly List<HttpClient> _clients = [];

    public TestHttpClientFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        _handler = new DelegateHandler(send);
        _handler.Capture = async (request, cancellationToken) =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests)
            {
                Requests.Add(new CapturedRequest(request.Method, request.RequestUri!, request.Content?.Headers.ContentType?.MediaType, ParseForm(body)));
            }
        };
    }

    public List<CapturedRequest> Requests { get; } = [];

    public static TestHttpClientFactory Responding(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new TestHttpClientFactory((_, _) => Task.FromResult(JsonResponse(json, status)));
    }

    public static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    public static Dictionary<string, string> ParseForm(string form)
    {
        return form.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair.Length == 2 ? pair[1] : ""), StringComparer.Ordinal);
    }

    public HttpClient CreateClient(string name)
    {
        var client = new HttpClient(_handler, disposeHandler: false);
        lock (_clients)
        {
            _clients.Add(client);
        }
        return client;
    }

    public void Dispose()
    {
        foreach (var client in _clients)
        {
            client.Dispose();
        }
        _handler.Dispose();
    }

    internal sealed record CapturedRequest(HttpMethod Method, Uri Uri, string? ContentType, Dictionary<string, string> Form);

    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public Func<HttpRequestMessage, CancellationToken, Task>? Capture { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Capture is not null)
            {
                await Capture(request, cancellationToken);
            }

            return await send(request, cancellationToken);
        }
    }
}
