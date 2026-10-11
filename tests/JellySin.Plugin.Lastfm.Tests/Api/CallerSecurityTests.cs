using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using JellySin.Plugin.Lastfm.Api;
using JellySin.Plugin.Lastfm.Tests.Core;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Api;

public sealed class CallerSecurityTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, false, true)]
    public async Task InvalidIdentityCannotReachAction(bool authenticated, bool apiKey, bool disabled, bool missingUser)
    {
        var identity = Identity();
        identity.IsAuthenticated = authenticated;
        identity.IsApiKey = apiKey;
        identity.User!.SetPermission(PermissionKind.IsDisabled, disabled);
        if (missingUser) identity.User = null;
        var result = await RunFilter(identity, "POST", null, null);
        Assert.IsType<UnauthorizedResult>(result.Context.Result);
        Assert.False(result.Called);
    }

    [Theory]
    [InlineData("https://evil.example", null)]
    [InlineData("http://jellyfin.example", null)]
    [InlineData("https://jellyfin.example:8443", null)]
    [InlineData("null", null)]
    [InlineData(null, "cross-site")]
    [InlineData("https://jellyfin.example", "same-site")]
    public async Task CrossOriginMutationIsForbidden(string? origin, string? fetchSite)
    {
        var result = await RunFilter(Identity(), "POST", origin, fetchSite);
        Assert.Equal(403, Assert.IsType<StatusCodeResult>(result.Context.Result).StatusCode);
        Assert.False(result.Called);
    }

    [Theory]
    [InlineData("POST", "https://jellyfin.example", "same-origin")]
    [InlineData("PUT", null, null)]
    [InlineData("GET", "https://elsewhere.example", "cross-site")]
    [InlineData("HEAD", null, null)]
    public async Task HeaderAuthenticatedRequestsKeepIdentityAndDisableCaching(string method, string? origin, string? fetchSite)
    {
        var identity = Identity();
        var result = await RunFilter(identity, method, origin, fetchSite);
        Assert.True(result.Called);
        Assert.Same(identity, result.Context.HttpContext.Items[typeof(CallerFilter)]);
        Assert.Equal("no-store", result.Context.HttpContext.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task AccountMutationUsesAuthenticatedIdentityDespiteForgedQueryUser()
    {
        using var fixture = new CoreFixture();
        var identity = Identity(fixture.UserId);
        var result = await RunFilter(identity, "POST", null, null);
        var otherUser = Guid.NewGuid();
        result.Context.HttpContext.Request.QueryString = new QueryString("?userId=" + otherUser);
        var controller = new AccountController(fixture.Accounts) { ControllerContext = new ControllerContext { HttpContext = result.Context.HttpContext } };
        await fixture.ConfigureApplicationAsync();
        var start = await controller.Begin(new ConnectionBegin(false), TestContext.Current.CancellationToken);
        Assert.NotNull(await fixture.Store.ReadAsync<JellySin.Plugin.Lastfm.Configuration.AccountService.StoredAttempt>(fixture.UserId, "attempt", TestContext.Current.CancellationToken));
        Assert.Null(await fixture.Store.ReadAsync<JellySin.Plugin.Lastfm.Configuration.AccountService.StoredAttempt>(otherUser, "attempt", TestContext.Current.CancellationToken));
        var binding = Microsoft.Net.Http.Headers.SetCookieHeaderValue.Parse(Assert.Single(controller.Response.Headers.SetCookie)!).Value.Value;
        Assert.True((await fixture.Accounts.ApproveAsync(binding, CoreFixture.AttemptOf(start.AuthorizationUrl), "browser-test-token", TestContext.Current.CancellationToken)).Accepted);
        Assert.False((await fixture.Accounts.GetStatusAsync(otherUser, TestContext.Current.CancellationToken)).ApprovalPending);
        var status = await controller.Finish(TestContext.Current.CancellationToken);
        Assert.True(status.Connected);
        await controller.Scrobbling(new ToggleRequest(false), TestContext.Current.CancellationToken);
        var response = System.Text.Json.JsonSerializer.SerializeToElement(await controller.GetMe(TestContext.Current.CancellationToken));
        Assert.Equal(identity.User!.Username, response.GetProperty("UserName").GetString());
        Assert.DoesNotContain("private-session-value", response.ToString(), StringComparison.Ordinal);
        Assert.IsType<NoContentResult>(await controller.Disconnect(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task OrdinaryUserCannotChangeOrReadApplicationCredentials()
    {
        using var fixture = new CoreFixture();
        var result = await RunFilter(Identity(), "PUT", null, null);
        var controller = new AdministrationController(fixture.Credentials) { ControllerContext = new ControllerContext { HttpContext = result.Context.HttpContext } };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => controller.Status(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => controller.Save(new ApplicationInput(new string('a', 32), new string('b', 32)), TestContext.Current.CancellationToken));
        await fixture.ConfigureApplicationAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => controller.Remove(TestContext.Current.CancellationToken));
        Assert.True((await fixture.Credentials.GetAsync(TestContext.Current.CancellationToken)).IsConfigured);
    }

    [Fact]
    public async Task AdministratorStatusNeverReturnsStoredCredentials()
    {
        using var fixture = new CoreFixture();
        var identity = Identity();
        identity.User!.SetPermission(PermissionKind.IsAdministrator, true);
        var result = await RunFilter(identity, "PUT", null, null);
        var controller = new AdministrationController(fixture.Credentials) { ControllerContext = new ControllerContext { HttpContext = result.Context.HttpContext } };
        var input = new ApplicationInput(new string('a', 32), new string('b', 32));
        Assert.IsType<NoContentResult>(await controller.Save(input, TestContext.Current.CancellationToken));
        Assert.Equal("{\"Configured\":true,\"Source\":\"Override\"}", System.Text.Json.JsonSerializer.Serialize(await controller.Status(TestContext.Current.CancellationToken)));
        Assert.DoesNotContain(input.Secret, input.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AdministratorCanRemoveTheApplicationOverride()
    {
        using var fixture = new CoreFixture();
        var identity = Identity();
        identity.User!.SetPermission(PermissionKind.IsAdministrator, true);
        var result = await RunFilter(identity, "DELETE", null, null);
        var controller = new AdministrationController(fixture.Credentials) { ControllerContext = new ControllerContext { HttpContext = result.Context.HttpContext } };
        Assert.Equal("{\"Configured\":false,\"Source\":\"None\"}", System.Text.Json.JsonSerializer.Serialize(await controller.Status(TestContext.Current.CancellationToken)));
        await fixture.ConfigureApplicationAsync();
        Assert.IsType<NoContentResult>(await controller.Remove(TestContext.Current.CancellationToken));
        Assert.Equal("{\"Configured\":false,\"Source\":\"None\"}", System.Text.Json.JsonSerializer.Serialize(await controller.Status(TestContext.Current.CancellationToken)));
        Assert.IsType<NoContentResult>(await controller.Remove(TestContext.Current.CancellationToken));
        var action = typeof(AdministrationController).GetMethod(nameof(AdministrationController.Remove))!;
        Assert.Contains(action.GetCustomAttributes(false), attribute => attribute is HttpDeleteAttribute { Template: "Application" });
    }

    [Fact]
    public void EveryPrivateControllerInheritsHostAuthorizationAndCallerFilter()
    {
        var controllers = typeof(PrivateController).Assembly.GetTypes().Where(type => type.IsSubclassOf(typeof(PrivateController))).ToArray();
        Assert.NotEmpty(controllers);
        foreach (var type in controllers)
        {
            Assert.Contains(type.GetCustomAttributes(true), attribute => attribute is AuthorizeAttribute);
            Assert.Contains(type.GetCustomAttributes(true), attribute => attribute is TypeFilterAttribute { ImplementationType: var filter } && filter == typeof(CallerFilter));
            Assert.DoesNotContain(type.GetCustomAttributes(true), attribute => attribute is AllowAnonymousAttribute);
        }
    }

    public static AuthorizationInfo Identity(Guid? userId = null) => new()
    {
        IsAuthenticated = true,
        Token = "private-jellyfin-token",
        User = new User("listener", "authentication", "password") { Id = userId ?? Guid.NewGuid() }
    };

    private static async Task<(ActionExecutingContext Context, bool Called)> RunFilter(AuthorizationInfo identity, string method, string? origin, string? fetchSite)
    {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("jellyfin.example");
        if (origin is not null) http.Request.Headers.Origin = origin;
        if (fetchSite is not null) http.Request.Headers["Sec-Fetch-Site"] = fetchSite;
        var action = new ActionContext(http, new RouteData(), new ActionDescriptor());
        var context = new ActionExecutingContext(action, [], new Dictionary<string, object?>(), new object());
        var authorization = new Mock<IAuthorizationContext>();
        authorization.Setup(value => value.GetAuthorizationInfo(http)).ReturnsAsync(identity);
        var called = false;
        await new CallerFilter(authorization.Object).OnActionExecutionAsync(context, () =>
        {
            called = true;
            return Task.FromResult(new ActionExecutedContext(action, [], new object()));
        });
        return (context, called);
    }
}
