using System.Net;
using System.Net.Http.Headers;
using JellySin.Plugin.Lastfm.Transport;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class TransportTests
{
    [Fact]
    public void SignatureUsesUtf8AndExcludesFormatCallbackAndSignature()
    {
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", LastfmSignature.Compute(new Dictionary<string, string>
        { ["a"] = "b", ["format"] = "json", ["callback"] = "ignore", ["api_sig"] = "ignore" }, "c"));
        Assert.Equal("bde5f5cae7cf353043b1656425156683", LastfmSignature.Compute(new Dictionary<string, string> { ["a"] = "é" }, "c"));
        Assert.Equal("c39bf8a6d47eff6d965c829f01b6d9c3", LastfmSignature.Compute(new Dictionary<string, string> { ["a[1]"] = "x", ["a[10]"] = "y" }, "z"));
    }

    [Fact]
    public async Task ApiErrorsInsideHttp200AreNotSuccess()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        using var client = Create(fixture, new Handler(_ => Response("{\"error\":9,\"message\":\"do not log me\"}")));
        await client.StartAsync(TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<LastfmException>(() => client.CallAsync("track.love", new Dictionary<string, string>(), "session", RequestPriority.Interactive, TestContext.Current.CancellationToken));
        Assert.Equal(9, error.Code);
        Assert.DoesNotContain("do not log me", error.ToString(), StringComparison.Ordinal);
        await client.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AuthenticatedCallsUseFormBodyAndNeverCredentialsInUrl()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var handler = new Handler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://ws.audioscrobbler.com/2.0/", request.RequestUri!.AbsoluteUri);
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            return Response("{}");
        });
        using var client = Create(fixture, handler);
        await client.StartAsync(TestContext.Current.CancellationToken);
        using var result = await client.CallAsync("track.love", new Dictionary<string, string> { ["artist"] = "A & B" }, "private", RequestPriority.Interactive, TestContext.Current.CancellationToken);
        await client.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task RateLimitAppliesAcrossAllPriorities()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var handler = new Handler(_ =>
        {
            var response = Response("{}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
            return response;
        });
        using var client = Create(fixture, handler);
        await client.StartAsync(TestContext.Current.CancellationToken);
        var first = await Assert.ThrowsAsync<LastfmException>(() => Call(client));
        Assert.Equal(29, first.Code);
        Assert.Equal(TimeSpan.FromSeconds(45), first.RetryAfter);
        await Assert.ThrowsAsync<LastfmException>(() => client.CallAsync("track.scrobble", new Dictionary<string, string>(), "session", RequestPriority.Listening, TestContext.Current.CancellationToken));
        Assert.Equal(1, handler.Count);
        await client.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExplicitHttpMaxAgeAvoidsRepeatRequestsUntilExpiry()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var handler = new Handler(_ =>
        {
            var response = Response("{\"artist\":{\"name\":\"cached\"}}");
            response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromSeconds(60) };
            return response;
        });
        using var client = Create(fixture, handler);
        await client.StartAsync(TestContext.Current.CancellationToken);
        using var first = await Call(client);
        using var second = await Call(client);
        Assert.Equal(1, handler.Count);
        fixture.Clock.Advance(61);
        using var third = await Call(client);
        Assert.Equal(2, handler.Count);
        await client.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task NoStoreResponseIsNotPersisted()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var handler = new Handler(_ =>
        {
            var response = Response("{\"private\":true}");
            response.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true, MaxAge = TimeSpan.FromMinutes(5) };
            return response;
        });
        using var client = Create(fixture, handler);
        await client.StartAsync(TestContext.Current.CancellationToken);
        using var first = await Call(client);
        using var second = await Call(client);
        Assert.Equal(2, handler.Count);
        Assert.False(client.CanPersist(first));
        Assert.False(client.CanPersist(second));
        Assert.Empty(Directory.EnumerateFiles(fixture.DirectoryPath, "cache-*.json", SearchOption.AllDirectories));
        await client.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task OversizedResponseIsRejectedBeforeParsing()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        using var client = Create(fixture, new Handler(_ => Response(new string('x', 2_000_001))));
        await client.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(8, (await Assert.ThrowsAsync<LastfmException>(() => Call(client))).Code);
        await client.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CancelledQueuedRequestNeverReachesNetwork()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var handler = new Handler(_ => Response("{}"));
        using var client = Create(fixture, handler);
        using var cancellation = new CancellationTokenSource();
        var pending = client.CallAsync("artist.getInfo", new Dictionary<string, string>(), null, RequestPriority.Background, cancellation.Token);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await client.StartAsync(TestContext.Current.CancellationToken);
        using var result = await Call(client);
        Assert.Equal(1, handler.Count);
        await client.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UnconfiguredApplicationMakesNoNetworkRequests()
    {
        using var fixture = new CoreFixture();
        var handler = new Handler(_ => throw new InvalidOperationException("Network must remain unused."));
        using var client = Create(fixture, handler);
        await client.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(10, (await Assert.ThrowsAsync<LastfmException>(() => Call(client))).Code);
        Assert.Equal(0, handler.Count);
        await client.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, 6)]
    [InlineData(HttpStatusCode.BadGateway, 16)]
    [InlineData(HttpStatusCode.OK, 16)]
    public async Task NonJsonHttpErrorsHaveSafeRetryClassification(HttpStatusCode status, int code)
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        using var client = Create(fixture, new Handler(_ => Response("<html>upstream failure</html>", status)));
        await client.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(code, (await Assert.ThrowsAsync<LastfmException>(() => Call(client))).Code);
        await client.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PrivateUserReadsNeverEnterSharedHttpCache()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var handler = new Handler(_ =>
        {
            var response = Response("{\"recenttracks\":{}}");
            response.Headers.CacheControl = new CacheControlHeaderValue { MaxAge = TimeSpan.FromHours(1) };
            return response;
        });
        using var client = Create(fixture, handler);
        await client.StartAsync(TestContext.Current.CancellationToken);
        using var first = await client.CallAsync("user.getRecentTracks", new Dictionary<string, string> { ["user"] = "listener" }, null, RequestPriority.Background, TestContext.Current.CancellationToken);
        using var second = await client.CallAsync("user.getRecentTracks", new Dictionary<string, string> { ["user"] = "listener" }, null, RequestPriority.Background, TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Count);
        Assert.Empty(Directory.EnumerateFiles(fixture.DirectoryPath, "cache-*.json", SearchOption.AllDirectories));
        await client.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task QueueIsBoundedAndShutdownCompletesAllWaiters()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var handler = new Handler(_ => Response("{}"));
        using var client = Create(fixture, handler);
        var queued = Enumerable.Range(0, 128).Select(_ => Call(client)).ToArray();
        Assert.Equal(16, (await Assert.ThrowsAsync<LastfmException>(() => Call(client))).Code);
        await client.StartAsync(TestContext.Current.CancellationToken);
        await client.StopAsync(TestContext.Current.CancellationToken);
        foreach (var pending in queued)
        {
            try { using var result = await pending; }
            catch (OperationCanceledException) { }
        }
        Assert.All(queued, pending => Assert.True(pending.IsCompleted));
    }

    private static Task<System.Text.Json.JsonDocument> Call(LastfmClient client) => client.CallAsync("artist.getInfo", new Dictionary<string, string> { ["artist"] = "Artist" }, null, RequestPriority.Background, TestContext.Current.CancellationToken);
    private static LastfmClient Create(CoreFixture fixture, Handler handler) => new(new HttpClient(handler), fixture.Credentials, fixture.Store, fixture.Clock);
    private static HttpResponseMessage Response(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Count { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Count++; return Task.FromResult(respond(request)); }
    }
}
