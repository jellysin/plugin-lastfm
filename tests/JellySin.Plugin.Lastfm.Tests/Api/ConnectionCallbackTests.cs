using System.Text.Json;
using JellySin.Plugin.Lastfm.Api;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Tests.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace JellySin.Plugin.Lastfm.Tests.Api;

public sealed class ConnectionCallbackTests
{
    private const string Token = "browser-test-token";

    [Theory]
    [InlineData("https", "", "__Host-JellySinLastfmAttempt", "/")]
    [InlineData("https", "/jellyfin", "__Host-JellySinLastfmAttempt", "/")]
    [InlineData("http", "", "JellySinLastfmAttempt", "/JellySin/Lastfm/Callback")]
    [InlineData("http", "/jellyfin", "JellySinLastfmAttempt", "/jellyfin/JellySin/Lastfm/Callback")]
    public async Task BeginBindsTheAttemptToTheBrowserAndKeepsTheBindingFromPageScript(string scheme, string prefix, string name, string path)
    {
        using var fixture = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        var account = Account(fixture, scheme, prefix);
        var start = await account.Begin(new ConnectionBegin(false), TestContext.Current.CancellationToken);
        var cookie = SetCookieHeaderValue.Parse(Assert.Single(account.Response.Headers.SetCookie)!);
        Assert.Equal(name, cookie.Name.Value);
        Assert.Equal(path, cookie.Path.Value);
        Assert.True(cookie.HttpOnly);
        Assert.Equal(scheme == "https", cookie.Secure);
        Assert.Equal(Microsoft.Net.Http.Headers.SameSiteMode.Lax, cookie.SameSite);
        Assert.Equal(TimeSpan.FromMinutes(10), cookie.MaxAge);
        Assert.False(cookie.Domain.HasValue);
        var body = JsonSerializer.SerializeToElement(start);
        Assert.Equal(new[] { "AuthorizationUrl", "ExpiresAt" }, body.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.NotEmpty(cookie.Value.Value!);
        Assert.DoesNotContain(cookie.Value.Value!, body.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("api_key", start.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(start.AuthorizationUrl, start.ToString(), StringComparison.Ordinal);
        var callback = System.Web.HttpUtility.ParseQueryString(new Uri(start.AuthorizationUrl).Query)["cb"]!;
        Assert.StartsWith($"{scheme}://jellyfin.example{prefix}/JellySin/Lastfm/Callback?attempt=", callback, StringComparison.Ordinal);
        Assert.Empty(fixture.Client.Calls);
    }

    [Theory]
    [InlineData("https", "", false, "/JellySin/Lastfm/")]
    [InlineData("https", "/jellyfin", false, "/jellyfin/JellySin/Lastfm/")]
    [InlineData("http", "/jellyfin", true, "/jellyfin/web/index.html#/configurationpage?name=jellysin-lastfm")]
    public async Task ReturnFromLastfmParksTheTokenAndLeavesItOutOfTheNextAddress(string scheme, string prefix, bool settings, string target)
    {
        using var fixture = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        var account = Account(fixture, scheme, prefix);
        var start = await account.Begin(new ConnectionBegin(settings), TestContext.Current.CancellationToken);
        var issued = SetCookieHeaderValue.Parse(Assert.Single(account.Response.Headers.SetCookie)!);
        var callback = Callback(fixture, scheme, prefix, issued);
        var redirect = Assert.IsType<LocalRedirectResult>(await callback.Approve(CoreFixture.AttemptOf(start.AuthorizationUrl), Token, TestContext.Current.CancellationToken));
        Assert.Equal(target, redirect.Url);
        Assert.False(redirect.Permanent);
        Assert.DoesNotContain(Token, redirect.Url, StringComparison.Ordinal);
        Assert.Empty(fixture.Client.Calls);
        Assert.Equal("no-store", callback.Response.Headers.CacheControl);
        Assert.Equal("no-referrer", callback.Response.Headers["Referrer-Policy"]);
        Assert.Equal("nosniff", callback.Response.Headers["X-Content-Type-Options"]);
        AssertCleared(callback, issued);
        Assert.True((await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).ApprovalPending);
        Assert.True((await account.Finish(TestContext.Current.CancellationToken)).Connected);
        Assert.Equal(Token, Assert.Single(fixture.Client.Calls).Values["token"]);
    }

    [Theory]
    [InlineData(false, "/jellyfin/JellySin/Lastfm/?connection=failed")]
    [InlineData(true, "/jellyfin/web/index.html#/configurationpage?name=jellysin-lastfm&connection=failed")]
    public async Task ARefusedReturnGoesBackToThePageThatStartedItWithoutAnApproval(bool settings, string target)
    {
        using var fixture = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        var account = Account(fixture, "https", "/jellyfin");
        var start = await account.Begin(new ConnectionBegin(settings), TestContext.Current.CancellationToken);
        var issued = SetCookieHeaderValue.Parse(Assert.Single(account.Response.Headers.SetCookie)!);
        var callback = Callback(fixture, "https", "/jellyfin", issued);
        var redirect = Assert.IsType<LocalRedirectResult>(await callback.Approve(CoreFixture.AttemptOf(start.AuthorizationUrl), "not a token", TestContext.Current.CancellationToken));
        Assert.Equal(target, redirect.Url);
        AssertCleared(callback, issued);
        Assert.False((await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).ApprovalPending);
        Assert.Empty(fixture.Client.Calls);
    }

    [Fact]
    public async Task AReturnInAnotherBrowserOrForAnotherUserIsRefused()
    {
        using var fixture = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        var other = Guid.NewGuid();
        var myAccount = Account(fixture, "https", "");
        var mine = await myAccount.Begin(new ConnectionBegin(false), TestContext.Current.CancellationToken);
        var issued = SetCookieHeaderValue.Parse(Assert.Single(myAccount.Response.Headers.SetCookie)!);
        var theirAccount = Account(fixture, "https", "", other);
        await theirAccount.Begin(new ConnectionBegin(false), TestContext.Current.CancellationToken);
        var theirs = SetCookieHeaderValue.Parse(Assert.Single(theirAccount.Response.Headers.SetCookie)!);
        var attempt = CoreFixture.AttemptOf(mine.AuthorizationUrl);
        foreach (var cookie in new[] { null, theirs })
        {
            var redirect = Assert.IsType<LocalRedirectResult>(await Callback(fixture, "https", "", cookie).Approve(attempt, Token, TestContext.Current.CancellationToken));
            Assert.Equal("/JellySin/Lastfm/?connection=failed", redirect.Url);
        }
        // The binding issued for an HTTPS address carries the host-only name; a plain HTTP return does not read it.
        Assert.Equal("/JellySin/Lastfm/?connection=failed",
            Assert.IsType<LocalRedirectResult>(await Callback(fixture, "http", "", issued).Approve(attempt, Token, TestContext.Current.CancellationToken)).Url);
        Assert.False((await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).ApprovalPending);
        Assert.False((await fixture.Accounts.GetStatusAsync(other, TestContext.Current.CancellationToken)).ApprovalPending);
        Assert.Equal("/JellySin/Lastfm/",
            Assert.IsType<LocalRedirectResult>(await Callback(fixture, "https", "", issued).Approve(attempt, Token, TestContext.Current.CancellationToken)).Url);
        Assert.True((await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).ApprovalPending);
        Assert.False((await fixture.Accounts.GetStatusAsync(other, TestContext.Current.CancellationToken)).ApprovalPending);
        Assert.Empty(fixture.Client.Calls);
    }

    [Fact]
    public async Task BeginRefusesARequestWithoutAUsableServerAddress()
    {
        using var fixture = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        var account = Account(fixture, "https", "");
        account.Request.Host = new HostString("");
        await Assert.ThrowsAsync<ArgumentException>(() => account.Begin(new ConnectionBegin(false), TestContext.Current.CancellationToken));
        Assert.False(account.Response.Headers.ContainsKey("Set-Cookie"));
        Assert.Null(await fixture.Store.ReadAsync<AccountService.StoredAttempt>(fixture.UserId, "attempt", TestContext.Current.CancellationToken));
    }

    // The server behind a proxy that ends TLS sees plain HTTP and its own inner host name; the published address is what browsers use.
    [Theory]
    [InlineData("https://media.example", "http", "jellyfin-internal:8096", "", "https://media.example/JellySin/Lastfm/Callback", true)]
    [InlineData(" https://media.example/ ", "http", "jellyfin-internal:8096", "/jellyfin", "https://media.example/jellyfin/JellySin/Lastfm/Callback", true)]
    [InlineData("https://media.example:8920/a/path?and=query", "http", "jellyfin-internal:8096", "/jellyfin", "https://media.example:8920/jellyfin/JellySin/Lastfm/Callback", true)]
    [InlineData("https://proxy-user:proxy-word@media.example", "http", "jellyfin-internal:8096", "", "https://media.example/JellySin/Lastfm/Callback", true)]
    [InlineData("HTTPS://MEDIA.example:443", "http", "jellyfin-internal:8096", "", "https://media.example/JellySin/Lastfm/Callback", true)]
    [InlineData("http://media.example:8096", "https", "attacker.example", "/jellyfin", "http://media.example:8096/jellyfin/JellySin/Lastfm/Callback", false)]
    public async Task APublishedAddressDecidesTheReturnAndTheCookieWhateverTheRequestSays(string published, string scheme, string host, string prefix, string expected, bool encrypted)
    {
        using var fixture = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        var account = Account(fixture, scheme, prefix, published: published, host: host);
        var start = await account.Begin(new ConnectionBegin(false), TestContext.Current.CancellationToken);
        var callback = System.Web.HttpUtility.ParseQueryString(new Uri(start.AuthorizationUrl).Query)["cb"]!;
        Assert.StartsWith($"{expected}?attempt=", callback, StringComparison.Ordinal);
        Assert.DoesNotContain(host, start.AuthorizationUrl, StringComparison.Ordinal);
        Assert.DoesNotContain("proxy-", start.AuthorizationUrl, StringComparison.Ordinal);
        var issued = SetCookieHeaderValue.Parse(Assert.Single(account.Response.Headers.SetCookie)!);
        Assert.Equal(encrypted ? "__Host-JellySinLastfmAttempt" : "JellySinLastfmAttempt", issued.Name.Value);
        Assert.Equal(encrypted, issued.Secure);
        Assert.Equal(encrypted ? "/" : $"{prefix}/JellySin/Lastfm/Callback", issued.Path.Value);
        Assert.False(issued.Domain.HasValue);
        // The return arrives the same way as the start did: the cookie is found under the name the published scheme gave it.
        var returned = Callback(fixture, scheme, prefix, issued, published, host);
        var redirect = Assert.IsType<LocalRedirectResult>(await returned.Approve(CoreFixture.AttemptOf(start.AuthorizationUrl), Token, TestContext.Current.CancellationToken));
        Assert.Equal($"{prefix}/JellySin/Lastfm/", redirect.Url);
        AssertCleared(returned, issued);
        Assert.True((await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).ApprovalPending);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WithoutAPublishedAddressTheRequestNamesTheReturn(string? published)
    {
        using var fixture = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        var account = Account(fixture, "http", "/jellyfin", published: published, host: "192.168.1.20:8096");
        var start = await account.Begin(new ConnectionBegin(false), TestContext.Current.CancellationToken);
        Assert.StartsWith("http://192.168.1.20:8096/jellyfin/JellySin/Lastfm/Callback?attempt=",
            System.Web.HttpUtility.ParseQueryString(new Uri(start.AuthorizationUrl).Query)["cb"]!, StringComparison.Ordinal);
        Assert.Equal("JellySinLastfmAttempt", SetCookieHeaderValue.Parse(Assert.Single(account.Response.Headers.SetCookie)!).Name.Value);
    }

    // A published address that is set but unusable is not replaced by the request's address without a word.
    [Theory]
    [InlineData("media.example")]
    [InlineData("//media.example")]
    [InlineData("ftp://media.example")]
    [InlineData("https://")]
    [InlineData("https:///jellyfin")]
    public async Task AnUnusablePublishedAddressRefusesTheSignInAndIsNamedOnceInTheLogWithoutItsValue(string published)
    {
        using var fixture = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        var warnings = new Warnings();
        var server = new ServerAddress(published, warnings);
        var http = Request("POST", "https", "", "jellyfin.example");
        http.Items[typeof(CallerFilter)] = CallerSecurityTests.Identity(fixture.UserId);
        var account = new AccountController(fixture.Accounts, server) { ControllerContext = new ControllerContext { HttpContext = http } };
        await Assert.ThrowsAsync<ArgumentException>(() => account.Begin(new ConnectionBegin(false), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => account.Begin(new ConnectionBegin(false), TestContext.Current.CancellationToken));
        Assert.False(account.Response.Headers.ContainsKey("Set-Cookie"));
        Assert.Null(await fixture.Store.ReadAsync<AccountService.StoredAttempt>(fixture.UserId, "attempt", TestContext.Current.CancellationToken));
        var line = Assert.Single(warnings.Lines);
        Assert.Contains("published-server-url", line, StringComparison.Ordinal);
        Assert.DoesNotContain("media.example", line, StringComparison.Ordinal);
        // The anonymous return answers as for any attempt that never began.
        var returned = new CallbackController(fixture.Accounts, server) { ControllerContext = new ControllerContext { HttpContext = Request("GET", "https", "", "jellyfin.example") } };
        Assert.Equal("/JellySin/Lastfm/?connection=failed",
            Assert.IsType<LocalRedirectResult>(await returned.Approve("0123456789abcdef0123456789abcdef", Token, TestContext.Current.CancellationToken)).Url);
    }

    [Fact]
    public void AUsablePublishedAddressLeavesTheLogQuiet()
    {
        var warnings = new Warnings();
        _ = new ServerAddress("https://media.example", warnings);
        _ = new ServerAddress(null, warnings);
        Assert.Empty(warnings.Lines);
    }

    [Fact]
    public void TheCallbackIsOneAnonymousGetOutsideThePrivateSurface()
    {
        var type = typeof(CallbackController);
        Assert.False(type.IsSubclassOf(typeof(PrivateController)));
        Assert.Contains(type.GetCustomAttributes(true), attribute => attribute is AllowAnonymousAttribute);
        Assert.Contains(type.GetCustomAttributes(true), attribute => attribute is RouteAttribute { Template: "JellySin/Lastfm/Callback" });
        Assert.Contains(type.GetCustomAttributes(true), attribute => attribute is TypeFilterAttribute { ImplementationType: var filter } && filter == typeof(ApiExceptionFilter));
        var actions = type.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly);
        var action = Assert.Single(actions);
        Assert.Equal(nameof(CallbackController.Approve), action.Name);
        Assert.IsType<HttpGetAttribute>(Assert.Single(action.GetCustomAttributes(true).OfType<HttpMethodAttribute>()));
    }

    private static void AssertCleared(CallbackController callback, SetCookieHeaderValue issued)
    {
        var cleared = SetCookieHeaderValue.Parse(Assert.Single(callback.Response.Headers.SetCookie)!);
        Assert.Equal(issued.Name.Value, cleared.Name.Value);
        Assert.Equal(issued.Path.Value, cleared.Path.Value);
        Assert.Equal(issued.Secure, cleared.Secure);
        Assert.Empty(cleared.Value.Value!);
        Assert.True(cleared.Expires < DateTimeOffset.UnixEpoch.AddDays(1));
    }

    private static AccountController Account(CoreFixture fixture, string scheme, string prefix, Guid? user = null, string? published = null, string host = "jellyfin.example")
    {
        var http = Request("POST", scheme, prefix, host);
        http.Items[typeof(CallerFilter)] = CallerSecurityTests.Identity(user ?? fixture.UserId);
        return new AccountController(fixture.Accounts, new ServerAddress(published)) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static CallbackController Callback(CoreFixture fixture, string scheme, string prefix, SetCookieHeaderValue? cookie, string? published = null, string host = "jellyfin.example")
    {
        var http = Request("GET", scheme, prefix, host);
        if (cookie is not null) http.Request.Headers.Cookie = $"{cookie.Name.Value}={cookie.Value.Value}";
        return new CallbackController(fixture.Accounts, new ServerAddress(published)) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static DefaultHttpContext Request(string method, string scheme, string prefix, string host)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Scheme = scheme;
        http.Request.Host = new HostString(host);
        http.Request.PathBase = prefix;
        return http;
    }

    private sealed class Warnings : ILogger<ServerAddress>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Lines.Add(formatter(state, exception));
        }
    }
}
