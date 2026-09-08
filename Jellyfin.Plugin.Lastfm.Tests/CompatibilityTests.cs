using System.Reflection;
using System.Text.Json;
using System.Xml.Serialization;
using Jellyfin.Plugin.Lastfm.Api;
using Jellyfin.Plugin.Lastfm.Configuration;
using Jellyfin.Plugin.Lastfm.Models;
using MediaBrowser.Model.Updates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Jellyfin.Plugin.Lastfm.Tests;

public sealed class CompatibilityTests
{
    [Fact]
    public void LegacyConfigurationRetainsUserIdentitySessionAndOptions()
    {
        const string xml = """
            <PluginConfiguration>
              <LastfmUsers><LastfmUser>
                <Username>legacy-listener</Username><SessionKey>fake-legacy-session</SessionKey>
                <MediaBrowserUserId>4e155242-b88b-456d-b790-72565a7fd968</MediaBrowserUserId>
                <Options><Scrobble>true</Scrobble><SyncFavourites>true</SyncFavourites><AlternativeMode>true</AlternativeMode></Options>
              </LastfmUser></LastfmUsers>
            </PluginConfiguration>
            """;
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader(xml);
        var configuration = serializer.Deserialize(reader).ShouldBeOfType<PluginConfiguration>();
        var user = configuration.LastfmUsers.ShouldHaveSingleItem();
        user.Username.ShouldBe("legacy-listener");
        user.SessionKey.ShouldBe("fake-legacy-session");
        user.MediaBrowserUserId.ShouldBe(Guid.Parse("4e155242-b88b-456d-b790-72565a7fd968"));
        user.Options.Scrobble.ShouldBeTrue();
        user.Options.SyncFavourites.ShouldBeTrue();
        user.Options.AlternativeMode.ShouldBeTrue();
        typeof(LastfmUser).GetProperty("Password").ShouldBeNull();
    }

    [Fact]
    public void OldConfigurationWithoutOptionsGetsSafeDefaults()
    {
        using var reader = new StringReader("<PluginConfiguration><LastfmUsers><LastfmUser><Username>legacy</Username></LastfmUser></LastfmUsers></PluginConfiguration>");
        var configuration = new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader).ShouldBeOfType<PluginConfiguration>();
        var options = configuration.LastfmUsers.ShouldHaveSingleItem().Options;
        options.ShouldNotBeNull();
        options.Scrobble.ShouldBeFalse();
        options.SyncFavourites.ShouldBeFalse();
    }

    [Fact]
    public async Task CheckedInCatalogDeserializesIntoRealJellyfinPackageTypes()
    {
        var json = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "manifest.json"), TestContext.Current.CancellationToken);
        var packages = JsonSerializer.Deserialize<PackageInfo[]>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        packages.ShouldNotBeNull();
        var package = packages.ShouldHaveSingleItem();
        package.Id.ShouldBe(Guid.Parse("5e7fe7f0-b048-429e-a431-b1a7e69c930d"));
        package.Name.ShouldBe("Last.fm");
        package.Versions.ShouldNotBeEmpty();
        foreach (var version in package.Versions)
        {
            version.VersionNumber.ShouldNotBeNull();
            Version.TryParse(version.TargetAbi, out _).ShouldBeTrue();
            Uri.TryCreate(version.SourceUrl, UriKind.Absolute, out var uri).ShouldBeTrue();
            uri!.Scheme.ShouldBe("https");
        }
    }

    [Fact]
    public void LoginEndpointRequiresAdministratorPolicyAndKeepsItsRoute()
    {
        var controller = typeof(RestApi);
        controller.GetCustomAttribute<AuthorizeAttribute>()?.Policy.ShouldBe("RequiresElevation");
        controller.GetCustomAttribute<RouteAttribute>()?.Template.ShouldBe("Lastfm/Login");
        controller.GetCustomAttribute<AllowAnonymousAttribute>().ShouldBeNull();
        controller.GetMethod(nameof(RestApi.CreateMobileSession))?.GetCustomAttribute<HttpPostAttribute>().ShouldNotBeNull();
    }

    [Theory]
    [InlineData("", "password")]
    [InlineData("listener", "")]
    public async Task MissingCredentialsFailBeforeContactingLastfm(string username, string password)
    {
        using var factory = TestHttpClientFactory.Responding("{}");
        var controller = new RestApi(factory, NullLoggerFactory.Instance);
        var result = await controller.CreateMobileSession(new LastFMUser { Username = username, Password = password }, TestContext.Current.CancellationToken);
        result.Result.ShouldBeOfType<BadRequestObjectResult>();
        factory.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task FailedLoginDoesNotReflectRemoteMessageOrPassword()
    {
        const string untrustedMessage = "remote diagnostic with sensitive content";
        using var factory = TestHttpClientFactory.Responding($$"""{"error":4,"message":"{{untrustedMessage}}"}""");
        var controller = new RestApi(factory, NullLoggerFactory.Instance);
        var result = await controller.CreateMobileSession(new LastFMUser { Username = "listener", Password = "fake-password" }, TestContext.Current.CancellationToken);
        result.Value.ShouldNotBeNull();
        result.Value.ErrorCode.ShouldBe(4);
        result.Value.Message.ShouldNotContain(untrustedMessage);
        result.Value.Message.ShouldNotContain("fake-password");
        result.Value.Session.ShouldBeNull();
    }

    [Fact]
    public async Task LoginTransportFailureUsesGatewayStatus()
    {
        using var factory = TestHttpClientFactory.Responding("<html>unavailable</html>", System.Net.HttpStatusCode.ServiceUnavailable);
        var controller = new RestApi(factory, NullLoggerFactory.Instance);
        var result = await controller.CreateMobileSession(new LastFMUser { Username = "listener", Password = "fake-password" }, TestContext.Current.CancellationToken);
        result.Result.ShouldBeOfType<ObjectResult>().StatusCode.ShouldBe(502);
    }
}
