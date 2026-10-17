using System.Net;
using System.Net.Http.Headers;
using JellySin.Plugin.Lastfm.Metadata;
using JellySin.Plugin.Lastfm.Tests.Core;
using static JellySin.Plugin.Lastfm.Tests.Metadata.FakeImageHost;

namespace JellySin.Plugin.Lastfm.Tests.Metadata;

public sealed class ImageDownloadTests : IDisposable
{
    private const int Cap = 10 * 1024 * 1024;
    private const double DeadlineSeconds = 30;

    private readonly CoreFixture _fixture = new();
    private readonly FakeImageHost _host = new();

    public void Dispose() => _fixture.Dispose();

    private LastfmImageProvider Provider() => new(_fixture.Client, _fixture.Credentials, _host, _fixture.Clock);

    private Task<HttpResponseMessage> Fetch(string url, CancellationToken? token = null) =>
        Provider().GetImageResponse(url, token ?? TestContext.Current.CancellationToken);

    [Theory]
    [InlineData("https://lastfm.freetls.fastly.net/i/u/300x300/2a96cbd8b46e442fc41c2b86b821562f.png")]
    [InlineData("https://lastfm.freetls.fastly.net/i/u/300x300/c6f59c1e5e7240a4c0d427abd71f3dbb.png")]
    [InlineData("https://lastfm.freetls.fastly.net/i/u/300x300/C6F59C1E5E7240A4C0D427ABD71F3DBB.JPG")]
    [InlineData("https://lastfm.freetls.fastly.net/i/u/300x300/%32a96cbd8b46e442fc41c2b86b821562f.gif")]
    [InlineData("https://lastfm.freetls.fastly.net/i/u/2a96cbd8b46e442fc41c2b86b821562f.webp?size=large")]
    [InlineData("http://lastfm.freetls.fastly.net/i/u/300x300/0f1e2d3c4b5a69788796a5b4c3d2e1f0.png")]
    [InlineData("https://example.org/i/u/300x300/0f1e2d3c4b5a69788796a5b4c3d2e1f0.png")]
    [InlineData("https://lastfm.freetls.fastly.net@evil.example/x.png")]
    [InlineData("https://lastfm.freetls.fastly.net:8443/i/u/300x300/0f1e2d3c4b5a69788796a5b4c3d2e1f0.png")]
    [InlineData("https://user@lastfm.freetls.fastly.net/i/u/300x300/0f1e2d3c4b5a69788796a5b4c3d2e1f0.png")]
    [InlineData("https://lastfm.freetls.fastly.net.evil.example/i/u/300x300/0f1e2d3c4b5a69788796a5b4c3d2e1f0.png")]
    [InlineData("ftp://lastfm.freetls.fastly.net/i/u/300x300/0f1e2d3c4b5a69788796a5b4c3d2e1f0.png")]
    [InlineData("/i/u/300x300/0f1e2d3c4b5a69788796a5b4c3d2e1f0.png")]
    [InlineData("")]
    public async Task AnAddressThatIsNotFetchableIsRefusedWithoutARequest(string url)
    {
        using var response = await Fetch(url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_host.Requests);
        Assert.Empty(_host.ClientNames);
    }

    [Fact]
    public async Task AFetchableAddressIsFetchedThroughTheHostsDefaultClientWithoutCredentials()
    {
        var url = Url(Real, "300x300", "jpg") + "?v=2";
        _host.Respond = (_, _) => Task.FromResult(Image([9, 8, 7], "image/jpeg"));

        using var response = await Fetch(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(new byte[] { 9, 8, 7 }, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal(url, Assert.Single(_host.Requests).AbsoluteUri);
        Assert.Equal("Default", Assert.Single(_host.ClientNames));
    }

    [Theory]
    [InlineData("https://evil.example/x.png")]
    [InlineData("http://lastfm.freetls.fastly.net/i/u/300x300/0f1e2d3c4b5a69788796a5b4c3d2e1f0.png")]
    [InlineData("https://lastfm.freetls.fastly.net/i/u/300x300/2a96cbd8b46e442fc41c2b86b821562f.png")]
    public async Task AnAnswerThatCameFromARedirectOffTheImageHostIsRefused(string final)
    {
        _host.Respond = (request, _) =>
        {
            var response = Image([1, 2, 3]);
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, final);
            return Task.FromResult(response);
        };

        using var response = await Fetch(Url(Real));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AContentLengthOverTheCapIsRefused()
    {
        _host.Respond = (_, _) =>
        {
            var response = Image([1]);
            response.Content = new StreamContent(new MemoryStream([1]))
            {
                Headers = { ContentType = new MediaTypeHeaderValue("image/png"), ContentLength = Cap + 1 },
            };
            return Task.FromResult(response);
        };

        using var response = await Fetch(Url(Real));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ABodyWithoutALengthIsReadOnlyUpToTheCap()
    {
        var body = new EndlessStream();
        _host.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(body) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } },
        });

        using var response = await Fetch(Url(Real));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.InRange(body.BytesRead, Cap + 1, Cap + 200_000);
    }

    [Fact]
    public async Task ABodyOfExactlyTheCapIsKept()
    {
        _host.Respond = (_, _) => Task.FromResult(Image(new byte[Cap]));

        using var response = await Fetch(Url(Real));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Cap, (await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).Length);
    }

    [Theory]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    [InlineData("application/json")]
    public async Task ABodyThatIsNotAnImageIsRefused(string type)
    {
        _host.Respond = (_, _) => Task.FromResult(Image([1, 2, 3], type));

        using var response = await Fetch(Url(Real));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ABodyWithoutAContentTypeIsRefused()
    {
        _host.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) });

        using var response = await Fetch(Url(Real));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnEmptyBodyIsRefused()
    {
        _host.Respond = (_, _) => Task.FromResult(Image([]));

        using var response = await Fetch(Url(Real));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task AnUnsuccessfulStatusIsPassedOnWithoutItsBody(HttpStatusCode status)
    {
        _host.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("<html>") });

        using var response = await Fetch(Url(Real));

        Assert.Equal(status, response.StatusCode);
    }

    [Fact]
    public async Task ARequestThatOutlastsTheDeadlineFailsAsAnHttpRequest()
    {
        _host.Respond = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Image([1]);
        };

        var pending = Fetch(Url(Real));
        Assert.False(pending.IsCompleted);
        _fixture.Clock.Advance(DeadlineSeconds + 1);

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => pending);
        Assert.IsAssignableFrom<OperationCanceledException>(failure.InnerException);
    }

    [Fact]
    public async Task ARequestBelowTheDeadlineIsNotCut()
    {
        _host.Respond = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Image([1]);
        };

        var pending = Fetch(Url(Real));
        _fixture.Clock.Advance(DeadlineSeconds - 1);
        Assert.False(pending.IsCompleted);

        _fixture.Clock.Advance(2);
        await Assert.ThrowsAsync<HttpRequestException>(() => pending);
    }

    [Fact]
    public async Task TheCallersCancellationIsPassedThroughAndIsNotADeadline()
    {
        using var cancelled = new CancellationTokenSource();
        _host.Respond = async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Image([1]);
        };

        var pending = Fetch(Url(Real), cancelled.Token);
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task ACancelledCallerMakesNoRequest()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Fetch(Url(Real), cancelled.Token));

        Assert.Empty(_host.Requests);
    }
}
