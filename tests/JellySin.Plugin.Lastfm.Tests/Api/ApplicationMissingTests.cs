using System.Net;
using JellySin.Plugin.Lastfm.Api;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Tests.Core;
using JellySin.Plugin.Lastfm.Transport;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellySin.Plugin.Lastfm.Tests.Api;

/// <summary>Without a usable Last.fm application, a listener's request answers with its own status and mark, not as a connection failure.</summary>
public sealed class ApplicationMissingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ApplicationSource.None)]
    [InlineData(ApplicationSource.Unreadable)]
    public async Task ConnectingAnswersApplicationMissing(ApplicationSource source)
    {
        using var fixture = new CoreFixture();
        var (credentials, protection) = await ApplicationAsync(fixture, source);
        using var accounts = new AccountService(fixture.Store, fixture.Client, credentials, protection, fixture.Clock);

        var error = await Assert.ThrowsAsync<ApplicationMissingException>(() => accounts.BeginAsync(fixture.UserId, CoreFixture.Callback, false, Ct));

        AssertAnswer(error);
    }

    [Theory]
    [InlineData(ApplicationSource.None)]
    [InlineData(ApplicationSource.Unreadable)]
    public async Task LastfmCallsAnswerApplicationMissingWithoutReachingLastfm(ApplicationSource source)
    {
        using var fixture = new CoreFixture();
        var (credentials, _) = await ApplicationAsync(fixture, source);
        var sent = 0;
        using var http = new HttpClient(new Handler(() => sent++));
        using var client = new LastfmClient(http, credentials, fixture.Store, fixture.Clock);
        await client.StartAsync(Ct);

        var error = await Assert.ThrowsAsync<ApplicationMissingException>(() => client.CallAsync("track.love", new Dictionary<string, string>(), "session", RequestPriority.Interactive, Ct));
        await client.StopAsync(Ct);

        Assert.Equal(0, sent);
        // Stored plays keep treating the refusal as Last.fm's code 10.
        Assert.Equal(10, error.Code);
        AssertAnswer(error);
    }

    [Fact]
    public void ErrorTenFromLastfmItselfStaysARemoteFailure()
    {
        var result = Answer(new LastfmException(10));
        Assert.Equal(502, result.StatusCode);
        Assert.Null(Assert.IsType<ProblemDetails>(result.Value).Type);
    }

    private static async Task<(ApplicationCredentialService Credentials, IDataProtectionProvider Protection)> ApplicationAsync(CoreFixture fixture, ApplicationSource source)
    {
        if (source == ApplicationSource.None)
        {
            Assert.Equal(ApplicationSource.None, await fixture.Credentials.GetSourceAsync(Ct));
            return (fixture.Credentials, fixture.Protection);
        }
        await fixture.ConfigureApplicationAsync();
        // A configuration restored without jellysin-lastfm/keys: the stored override, read with a key ring that never protected it.
        var restored = new EphemeralDataProtectionProvider();
        var credentials = new ApplicationCredentialService(fixture.Store, restored);
        Assert.Equal(ApplicationSource.Unreadable, await credentials.GetSourceAsync(Ct));
        return (credentials, restored);
    }

    private static void AssertAnswer(Exception error)
    {
        var result = Answer(error);
        Assert.Equal(503, result.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(result.Value);
        Assert.Equal(ApiExceptionFilter.ApplicationMissingType, problem.Type);
        Assert.DoesNotContain("connection", problem.Title!, StringComparison.OrdinalIgnoreCase);
    }

    private static ObjectResult Answer(Exception error)
    {
        var context = new ExceptionContext(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), []) { Exception = error };
        new ApiExceptionFilter(NullLogger<ApiExceptionFilter>.Instance).OnException(context);
        return Assert.IsType<ObjectResult>(context.Result);
    }

    private sealed class Handler(Action sent) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            sent();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }
}
