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
        var account = new AccountController(fixture.Accounts, server, new LogLines<AccountController>()) { ControllerContext = new ControllerContext { HttpContext = http } };
        await Assert.ThrowsAsync<ArgumentException>(() => account.Begin(new ConnectionBegin(false), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => account.Begin(new ConnectionBegin(false), TestContext.Current.CancellationToken));
        Assert.False(account.Response.Headers.ContainsKey("Set-Cookie"));
        Assert.Null(await fixture.Store.ReadAsync<AccountService.StoredAttempt>(fixture.UserId, "attempt", TestContext.Current.CancellationToken));
        var line = Assert.Single(warnings.Lines);
        Assert.Contains("published-server-url", line, StringComparison.Ordinal);
        Assert.DoesNotContain("media.example", line, StringComparison.Ordinal);
        // The anonymous return answers as for any attempt that never began.
        var returned = new CallbackController(fixture.Accounts, server, new ReturnLogLimiter(fixture.Clock), new LogLines<CallbackController>()) { ControllerContext = new ControllerContext { HttpContext = Request("GET", "https", "", "jellyfin.example") } };
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

    // What an administrator reads after a listener says the button does nothing: one line a step, each with its outcome.
    [Fact]
    public async Task EachStepOfASignInLeavesOneLineThatNamesTheOutcomeAndNoSecret()
    {
        using var fixture = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        var name = Logged.User(fixture.UserId);
        var steps = new LogLines<AccountController>();
        var returns = new LogLines<CallbackController>();
        var account = Account(fixture, "http", "", published: "https://media.example", host: "jellyfin-internal:8096", log: steps);
        account.Request.Headers.Origin = "https://media.example";
        var start = await account.Begin(new ConnectionBegin(true), TestContext.Current.CancellationToken);
        Assert.Equal((LogLevel.Information, $"Last.fm sign-in begun for user {name} from the settings page. The return address uses https; the request reached this server over http; the page is at the published server address."),
            Assert.Single(steps.Lines));
        var issued = SetCookieHeaderValue.Parse(Assert.Single(account.Response.Headers.SetCookie)!);
        var attempt = CoreFixture.AttemptOf(start.AuthorizationUrl);
        await Callback(fixture, "http", "", issued, "https://media.example", "jellyfin-internal:8096", returns).Approve(attempt, Token, TestContext.Current.CancellationToken);
        Assert.Equal((LogLevel.Information, $"Last.fm return accepted for user {name}; the browser goes back to the settings page."), Assert.Single(returns.Lines));
        await Callback(fixture, "http", "", issued, "https://media.example", "jellyfin-internal:8096", returns).Approve(attempt, Token, TestContext.Current.CancellationToken);
        Assert.Equal((LogLevel.Information, $"Last.fm return refused for user {name}: the attempt was returned before. The browser goes back to the settings page."), returns.Lines[1]);
        Assert.True((await account.Finish(TestContext.Current.CancellationToken)).Connected);
        Assert.Equal((LogLevel.Information, $"Last.fm sign-in finished for user {name}: connected."), steps.Lines[1]);
        Assert.Equal(2, steps.Lines.Count);
        Assert.Equal(2, returns.Lines.Count);
        var written = string.Join('\n', steps.Lines.Concat(returns.Lines).Select(line => line.Text));
        foreach (var secret in new[] { Token, attempt, issued.Value.Value!, fixture.UserId.ToString("N"), fixture.UserId.ToString(), "api_key", "media.example", "jellyfin-internal", "private-" })
            Assert.DoesNotContain(secret, written, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("^[0-9a-f]{12}$", name);
    }

    [Theory]
    [InlineData("no application", "no Last.fm application is available")]
    [InlineData("no address", "the server has no usable return address")]
    [InlineData("cancelled", "the request was cancelled")]
    public async Task ASignInThatDoesNotBeginLeavesOneLineWithTheReason(string fault, string reason)
    {
        using var fixture = new CoreFixture();
        if (fault != "no application") await fixture.ConfigureApplicationAsync();
        var steps = new LogLines<AccountController>();
        var account = Account(fixture, "http", "", host: fault == "no address" ? "" : "192.168.1.20:8096", log: steps);
        using var cancelled = new CancellationTokenSource();
        if (fault == "cancelled") await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => account.Begin(new ConnectionBegin(false), cancelled.Token));
        Assert.Equal((LogLevel.Information, $"Last.fm sign-in not begun for user {Logged.User(fixture.UserId)} from the music dashboard: {reason}. The request reached this server over http; no published server address is set."),
            Assert.Single(steps.Lines));
        Assert.False(account.Response.Headers.ContainsKey("Set-Cookie"));
    }

    [Theory]
    [InlineData("return-address", "the return address the server names is not at the address of the page")]
    [InlineData("approval-address", "the server named no address of the Last.fm approval page")]
    public void AClickThePageRefusesLeavesExactlyOneLineWithTheReason(string reason, string phrase)
    {
        using var fixture = new CoreFixture();
        var steps = new LogLines<AccountController>();
        var account = Account(fixture, "http", "", published: "https://media.example", host: "192.168.1.20:8096", log: steps);
        account.Request.Headers.Origin = "http://192.168.1.20:8096";
        Assert.IsType<NoContentResult>(account.Refusal(new ConnectionRefusal(reason)));
        Assert.Equal((LogLevel.Information, $"The page did not open Last.fm for user {Logged.User(fixture.UserId)}: {phrase}. The request reached this server over http; the page is not at the published server address."),
            Assert.Single(steps.Lines));
        var action = typeof(AccountController).GetMethod(nameof(AccountController.Refusal))!;
        Assert.Contains(action.GetCustomAttributes(false), attribute => attribute is HttpPostAttribute { Template: "Connection/Refusal" });
    }

    // The log gets a phrase the server holds for a name it knows; text from the browser never reaches it.
    [Theory]
    [InlineData("")]
    [InlineData("Return-Address")]
    [InlineData("return-address ")]
    [InlineData("return-address\nLast.fm sign-in begun for user 000000000000")]
    [InlineData("the page made this up")]
    public void AReasonTheServerDoesNotKnowIsRefusedAndLeavesNoLine(string reason)
    {
        using var fixture = new CoreFixture();
        var steps = new LogLines<AccountController>();
        Assert.IsType<BadRequestResult>(Account(fixture, "https", "", log: steps).Refusal(new ConnectionRefusal(reason)));
        Assert.Empty(steps.Lines);
    }

    [Fact]
    public async Task AReturnThatNamesNoUserLeavesItsReasonAndTheLinesAreBounded()
    {
        using var fixture = new CoreFixture();
        await fixture.ConfigureApplicationAsync();
        var returns = new LogLines<CallbackController>();
        var limiter = new ReturnLogLimiter(fixture.Clock);
        var attempt = Guid.NewGuid().ToString("N");
        await Callback(fixture, "http", "", null, log: returns, limiter: limiter).Approve(attempt, Token, TestContext.Current.CancellationToken);
        Assert.Equal((LogLevel.Information, "Last.fm return refused: the browser sent no cookie of an attempt. The request reached this server over http."), Assert.Single(returns.Lines));
        var forged = new SetCookieHeaderValue("__Host-JellySinLastfmAttempt", "forged-cookie-value");
        await Callback(fixture, "https", "", forged, log: returns, limiter: limiter).Approve(attempt, Token, TestContext.Current.CancellationToken);
        Assert.Equal((LogLevel.Information, "Last.fm return refused: the cookie of the attempt cannot be read. The request reached this server over https."), returns.Lines[1]);
        for (var index = 0; index < 30; index++)
            await Callback(fixture, "https", "", null, log: returns, limiter: limiter).Approve(attempt, Token, TestContext.Current.CancellationToken);
        Assert.Equal(20, returns.Lines.Count);
        // A return that names a user is that user's own sign-in and is always written.
        var account = Account(fixture, "https", "");
        var start = await account.Begin(new ConnectionBegin(false), TestContext.Current.CancellationToken);
        var issued = SetCookieHeaderValue.Parse(Assert.Single(account.Response.Headers.SetCookie)!);
        await Callback(fixture, "https", "", issued, log: returns, limiter: limiter).Approve(CoreFixture.AttemptOf(start.AuthorizationUrl), "not a token", TestContext.Current.CancellationToken);
        Assert.Equal((LogLevel.Information, $"Last.fm return refused for user {Logged.User(fixture.UserId)}: the return carries no usable token. The browser goes back to the music dashboard."), returns.Lines[20]);
        await Callback(fixture, "https", "", issued, log: returns, limiter: limiter).Approve(attempt, Token, TestContext.Current.CancellationToken);
        Assert.EndsWith(": the return names another attempt than the cookie. The browser goes back to the music dashboard.", returns.Lines[21].Text, StringComparison.Ordinal);
        fixture.Clock.Advance(59);
        await Callback(fixture, "https", "", null, log: returns, limiter: limiter).Approve(attempt, Token, TestContext.Current.CancellationToken);
        Assert.Equal(22, returns.Lines.Count);
        fixture.Clock.Advance(1);
        await Callback(fixture, "https", "", null, log: returns, limiter: limiter).Approve(attempt, Token, TestContext.Current.CancellationToken);
        Assert.Equal(23, returns.Lines.Count);
        var written = string.Join('\n', returns.Lines.Select(line => line.Text));
        foreach (var secret in new[] { Token, attempt, "forged-cookie-value", "not a token", "in-the-address", issued.Value.Value! })
            Assert.DoesNotContain(secret, written, StringComparison.Ordinal);
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

    private static AccountController Account(CoreFixture fixture, string scheme, string prefix, Guid? user = null, string? published = null, string host = "jellyfin.example",
        LogLines<AccountController>? log = null)
    {
        var http = Request("POST", scheme, prefix, host);
        http.Items[typeof(CallerFilter)] = CallerSecurityTests.Identity(user ?? fixture.UserId);
        return new AccountController(fixture.Accounts, new ServerAddress(published), log ?? new LogLines<AccountController>()) { ControllerContext = new ControllerContext { HttpContext = http } };
    }

    private static CallbackController Callback(CoreFixture fixture, string scheme, string prefix, SetCookieHeaderValue? cookie, string? published = null, string host = "jellyfin.example",
        LogLines<CallbackController>? log = null, ReturnLogLimiter? limiter = null)
    {
        var http = Request("GET", scheme, prefix, host);
        // What Last.fm appends to the return address; the controller receives the same values as arguments.
        http.Request.QueryString = new QueryString("?attempt=attempt-in-the-address&token=token-in-the-address");
        if (cookie is not null) http.Request.Headers.Cookie = $"{cookie.Name.Value}={cookie.Value.Value}";
        return new CallbackController(fixture.Accounts, new ServerAddress(published), limiter ?? new ReturnLogLimiter(fixture.Clock), log ?? new LogLines<CallbackController>())
        { ControllerContext = new ControllerContext { HttpContext = http } };
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
