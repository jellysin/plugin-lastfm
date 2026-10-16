using Jellyfin.Data.Queries;
using JellySin.Plugin.Lastfm.Api;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Playback;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Model.Devices;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class DeviceAndMediaTypeRuleTests
{
    [Fact]
    public async Task DeviceExcluded_NotAllowed()
    {
        using var fixture = new CoreFixture();
        var rules = new LibraryRuleService(fixture.Store);
        await rules.SetUserExcludedDevicesAsync(fixture.UserId, ["dev-excluded"], TestContext.Current.CancellationToken);
        var audio = new Audio { Id = Guid.NewGuid(), Name = "Track", Artists = ["Artist"], RunTimeTicks = TimeSpan.FromSeconds(120).Ticks };

        Assert.False(rules.IsScrobblingAllowed(fixture.UserId, audio, "dev-excluded"));
    }

    [Fact]
    public async Task OtherDevice_Allowed()
    {
        using var fixture = new CoreFixture();
        var rules = new LibraryRuleService(fixture.Store);
        await rules.SetUserExcludedDevicesAsync(fixture.UserId, ["dev-excluded"], TestContext.Current.CancellationToken);
        var audio = new Audio { Id = Guid.NewGuid(), Name = "Track", Artists = ["Artist"], RunTimeTicks = TimeSpan.FromSeconds(120).Ticks };

        Assert.True(rules.IsScrobblingAllowed(fixture.UserId, audio, "dev-other"));
    }

    [Fact]
    public async Task NullDeviceId_Allowed()
    {
        using var fixture = new CoreFixture();
        var rules = new LibraryRuleService(fixture.Store);
        await rules.SetUserExcludedDevicesAsync(fixture.UserId, ["dev-excluded"], TestContext.Current.CancellationToken);
        var audio = new Audio { Id = Guid.NewGuid(), Name = "Track", Artists = ["Artist"], RunTimeTicks = TimeSpan.FromSeconds(120).Ticks };

        Assert.True(rules.IsScrobblingAllowed(fixture.UserId, audio, null));
    }

    [Fact]
    public async Task UserExcludedType_NotAllowed()
    {
        using var fixture = new CoreFixture();
        var config = new PluginConfiguration { ScrobbleMusic = true, ScrobbleAudiobooks = true };
        var rules = new LibraryRuleService(fixture.Store, configProvider: () => config);
        await rules.SetUserExcludedMediaTypesAsync(fixture.UserId, ["Audiobooks"], TestContext.Current.CancellationToken);

        var audiobook = new AudioBook { Id = Guid.NewGuid(), Name = "Chapter 1", Artists = ["Narrator"], RunTimeTicks = TimeSpan.FromSeconds(120).Ticks };
        Assert.False(rules.IsScrobblingAllowed(fixture.UserId, audiobook));

        var music = new Audio { Id = Guid.NewGuid(), Name = "Track", Artists = ["Artist"], RunTimeTicks = TimeSpan.FromSeconds(120).Ticks };
        Assert.True(rules.IsScrobblingAllowed(fixture.UserId, music));
    }

    [Fact]
    public async Task AdminOffType_StaysOffEvenIfUserDoesNotExclude()
    {
        using var fixture = new CoreFixture();
        var config = new PluginConfiguration { ScrobbleMusic = true, ScrobbleAudiobooks = false };
        var rules = new LibraryRuleService(fixture.Store, configProvider: () => config);
        await rules.SetUserExcludedMediaTypesAsync(fixture.UserId, [], TestContext.Current.CancellationToken);

        var audiobook = new AudioBook { Id = Guid.NewGuid(), Name = "Chapter 1", Artists = ["Narrator"], RunTimeTicks = TimeSpan.FromSeconds(120).Ticks };
        Assert.False(rules.IsMediaTypeAllowed(audiobook));

        var controller = new UserMediaTypesController(rules);
        var httpContext = new DefaultHttpContext();
        httpContext.Items[typeof(CallerFilter)] = Api.CallerSecurityTests.Identity(fixture.UserId);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var views = await controller.Get(TestContext.Current.CancellationToken);
        var audiobookView = Assert.Single(views, v => v.Type == "Audiobooks");
        Assert.False(audiobookView.IsAdminAllowed);
        Assert.False(audiobookView.IsUserExcluded);
        Assert.False(audiobookView.IsScrobbled);
    }

    [Fact]
    public async Task StoredRecordFromBefore_LoadsWithEmptyDevicesAndTypes()
    {
        using var fixture = new CoreFixture();
        var userDir = Path.Combine(fixture.DirectoryPath, fixture.UserId.ToString("N"));
        Directory.CreateDirectory(userDir);
        var oldLibId = Guid.NewGuid();
        await File.WriteAllTextAsync(
            Path.Combine(userDir, "library-rules.json"),
            $"{{\"ExcludedLibraries\":[\"{oldLibId}\"]}}",
            TestContext.Current.CancellationToken);

        var rules = new LibraryRuleService(fixture.Store);
        await rules.InitializeAsync(TestContext.Current.CancellationToken);

        var libraries = await rules.GetUserExcludedLibrariesAsync(fixture.UserId, TestContext.Current.CancellationToken);
        var devices = await rules.GetUserExcludedDevicesAsync(fixture.UserId, TestContext.Current.CancellationToken);
        var mediaTypes = await rules.GetUserExcludedMediaTypesAsync(fixture.UserId, TestContext.Current.CancellationToken);

        Assert.Equal([oldLibId], libraries);
        Assert.Empty(devices);
        Assert.Empty(mediaTypes);
    }

    [Fact]
    public async Task SettingLibraries_PreservesDevicesAndTypes_AndViceVersa()
    {
        using var fixture = new CoreFixture();
        var rules = new LibraryRuleService(fixture.Store);
        var lib1 = Guid.NewGuid();
        var lib2 = Guid.NewGuid();

        await rules.SetUserExcludedLibrariesAsync(fixture.UserId, [lib1], TestContext.Current.CancellationToken);
        await rules.SetUserExcludedDevicesAsync(fixture.UserId, ["dev1"], TestContext.Current.CancellationToken);
        await rules.SetUserExcludedMediaTypesAsync(fixture.UserId, ["Audiobooks"], TestContext.Current.CancellationToken);

        await rules.SetUserExcludedLibrariesAsync(fixture.UserId, [lib2], TestContext.Current.CancellationToken);
        Assert.Equal([lib2], await rules.GetUserExcludedLibrariesAsync(fixture.UserId, TestContext.Current.CancellationToken));
        Assert.Equal(["dev1"], await rules.GetUserExcludedDevicesAsync(fixture.UserId, TestContext.Current.CancellationToken));
        Assert.Equal(["Audiobooks"], await rules.GetUserExcludedMediaTypesAsync(fixture.UserId, TestContext.Current.CancellationToken));

        await rules.SetUserExcludedDevicesAsync(fixture.UserId, ["dev2"], TestContext.Current.CancellationToken);
        Assert.Equal([lib2], await rules.GetUserExcludedLibrariesAsync(fixture.UserId, TestContext.Current.CancellationToken));
        Assert.Equal(["dev2"], await rules.GetUserExcludedDevicesAsync(fixture.UserId, TestContext.Current.CancellationToken));
        Assert.Equal(["Audiobooks"], await rules.GetUserExcludedMediaTypesAsync(fixture.UserId, TestContext.Current.CancellationToken));

        await rules.SetUserExcludedMediaTypesAsync(fixture.UserId, ["MusicVideos"], TestContext.Current.CancellationToken);
        Assert.Equal([lib2], await rules.GetUserExcludedLibrariesAsync(fixture.UserId, TestContext.Current.CancellationToken));
        Assert.Equal(["dev2"], await rules.GetUserExcludedDevicesAsync(fixture.UserId, TestContext.Current.CancellationToken));
        Assert.Equal(["MusicVideos"], await rules.GetUserExcludedMediaTypesAsync(fixture.UserId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Bounds_DevicesCappedAt64_And257CharIdRefused()
    {
        using var fixture = new CoreFixture();
        var rules = new LibraryRuleService(fixture.Store);

        var excessDevices = Enumerable.Range(0, 65).Select(i => $"device-{i}").ToList();
        excessDevices.Add(new string('x', 257));

        await rules.SetUserExcludedDevicesAsync(fixture.UserId, excessDevices, TestContext.Current.CancellationToken);

        var stored = await rules.GetUserExcludedDevicesAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.Equal(64, stored.Count);
        Assert.DoesNotContain(new string('x', 257), stored);
        Assert.DoesNotContain("device-64", stored);
    }

    [Fact]
    public async Task RestartRecovery_LibraryRuleServiceOverSameStore_SeesExclusionsAfterInitializeAsync()
    {
        using var fixture = new CoreFixture();
        var rules1 = new LibraryRuleService(fixture.Store);
        var libId = Guid.NewGuid();
        await rules1.SetUserExcludedLibrariesAsync(fixture.UserId, [libId], TestContext.Current.CancellationToken);
        await rules1.SetUserExcludedDevicesAsync(fixture.UserId, ["dev-restart"], TestContext.Current.CancellationToken);
        await rules1.SetUserExcludedMediaTypesAsync(fixture.UserId, ["MusicVideos"], TestContext.Current.CancellationToken);

        var rules2 = new LibraryRuleService(fixture.Store);
        await rules2.InitializeAsync(TestContext.Current.CancellationToken);

        var libraries = await rules2.GetUserExcludedLibrariesAsync(fixture.UserId, TestContext.Current.CancellationToken);
        var devices = await rules2.GetUserExcludedDevicesAsync(fixture.UserId, TestContext.Current.CancellationToken);
        var mediaTypes = await rules2.GetUserExcludedMediaTypesAsync(fixture.UserId, TestContext.Current.CancellationToken);

        Assert.Equal([libId], libraries);
        Assert.Equal(["dev-restart"], devices);
        Assert.Equal(["MusicVideos"], mediaTypes);

        var mv = new MusicVideo { Id = Guid.NewGuid(), Name = "Video", Artists = ["Artist"], RunTimeTicks = TimeSpan.FromSeconds(120).Ticks };
        Assert.False(rules2.IsScrobblingAllowed(fixture.UserId, mv, "other-dev"));

        var music = new Audio { Id = Guid.NewGuid(), Name = "Song", Artists = ["Artist"], RunTimeTicks = TimeSpan.FromSeconds(120).Ticks };
        Assert.False(rules2.IsScrobblingAllowed(fixture.UserId, music, "dev-restart"));
    }

    [Fact]
    public async Task UserIsolation_UserAExclusionDoesNotAffectUserB()
    {
        using var fixture = new CoreFixture();
        var userB = Guid.NewGuid();
        var rules = new LibraryRuleService(fixture.Store);

        await rules.SetUserExcludedDevicesAsync(fixture.UserId, ["device-A"], TestContext.Current.CancellationToken);
        await rules.SetUserExcludedMediaTypesAsync(fixture.UserId, ["Music"], TestContext.Current.CancellationToken);

        var audio = new Audio { Id = Guid.NewGuid(), Name = "Song", Artists = ["Artist"], RunTimeTicks = TimeSpan.FromSeconds(120).Ticks };

        Assert.False(rules.IsScrobblingAllowed(fixture.UserId, audio, "device-A"));
        Assert.False(rules.IsScrobblingAllowed(fixture.UserId, audio, "device-other"));

        Assert.True(rules.IsScrobblingAllowed(userB, audio, "device-A"));
        Assert.True(rules.IsScrobblingAllowed(userB, audio, "device-other"));
    }

    [Fact]
    public async Task UserDevicesController_PutDropsIdsNotBelongingToCaller()
    {
        using var fixture = new CoreFixture();
        var deviceManager = new Mock<IDeviceManager>();
        var callerDevice1 = new DeviceInfo { Id = "dev-caller-1", Name = "Phone", CustomName = "My Phone", DateLastActivity = DateTime.UtcNow };
        var callerDevice2 = new DeviceInfo { Id = "dev-caller-2", Name = "Tablet", DateLastActivity = DateTime.UtcNow.AddMinutes(-5) };
        deviceManager.Setup(m => m.GetDeviceInfos(It.Is<DeviceQuery>(q => q.UserId == fixture.UserId))).Returns(new QueryResult<DeviceInfo>
        {
            Items = [callerDevice1, callerDevice2],
            TotalRecordCount = 2
        });

        var rules = new LibraryRuleService(fixture.Store);
        var controller = new UserDevicesController(deviceManager.Object, rules);
        var httpContext = new DefaultHttpContext();
        httpContext.Items[typeof(CallerFilter)] = Api.CallerSecurityTests.Identity(fixture.UserId);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var longId = new string('z', 257);
        var request = new UpdateUserDevicesRequest(["dev-caller-1", "foreign-device", longId]);
        var actionResult = await controller.Update(request, TestContext.Current.CancellationToken);

        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var resultList = Assert.IsAssignableFrom<IReadOnlyList<UserDeviceView>>(okResult.Value);

        var stored = await rules.GetUserExcludedDevicesAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.Equal(["dev-caller-1"], stored);

        var dev1View = Assert.Single(resultList, d => d.Id == "dev-caller-1");
        Assert.True(dev1View.IsExcluded);
        Assert.Equal("My Phone", dev1View.Name);

        var dev2View = Assert.Single(resultList, d => d.Id == "dev-caller-2");
        Assert.False(dev2View.IsExcluded);
    }

    [Fact]
    public async Task UserDevicesController_Get_CapsAt64NewestFirst()
    {
        using var fixture = new CoreFixture();
        var deviceManager = new Mock<IDeviceManager>();
        var now = DateTime.UtcNow;
        var devices = Enumerable.Range(0, 70).Select(i => new DeviceInfo
        {
            Id = $"dev-{i:D2}",
            Name = $"Device {i:D2}",
            DateLastActivity = now.AddMinutes(i)
        }).ToList();

        deviceManager.Setup(m => m.GetDeviceInfos(It.Is<DeviceQuery>(q => q.UserId == fixture.UserId))).Returns(new QueryResult<DeviceInfo>
        {
            Items = devices,
            TotalRecordCount = devices.Count
        });

        var rules = new LibraryRuleService(fixture.Store);
        var controller = new UserDevicesController(deviceManager.Object, rules);
        var httpContext = new DefaultHttpContext();
        httpContext.Items[typeof(CallerFilter)] = Api.CallerSecurityTests.Identity(fixture.UserId);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var actionResult = await controller.Get(TestContext.Current.CancellationToken);
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var list = Assert.IsAssignableFrom<IReadOnlyList<UserDeviceView>>(okResult.Value);

        Assert.Equal(64, list.Count);
        Assert.Equal("dev-69", list[0].Id);
    }

    [Fact]
    public async Task UserMediaTypesController_PutUnknownMediaType_ReturnsBadRequest()
    {
        using var fixture = new CoreFixture();
        var rules = new LibraryRuleService(fixture.Store);
        var controller = new UserMediaTypesController(rules);
        var httpContext = new DefaultHttpContext();
        httpContext.Items[typeof(CallerFilter)] = Api.CallerSecurityTests.Identity(fixture.UserId);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var badRequest = new UpdateUserMediaTypesRequest(["InvalidType", "Music"]);
        var result = await controller.Update(badRequest, TestContext.Current.CancellationToken);
        Assert.IsType<BadRequestObjectResult>(result.Result);

        Assert.Empty(await rules.GetUserExcludedMediaTypesAsync(fixture.UserId, TestContext.Current.CancellationToken));

        var validRequest = new UpdateUserMediaTypesRequest(["Music", "Audiobooks"]);
        var okResult = await controller.Update(validRequest, TestContext.Current.CancellationToken);
        var okObject = Assert.IsType<OkObjectResult>(okResult.Result);
        var views = Assert.IsAssignableFrom<IReadOnlyList<UserMediaTypeView>>(okObject.Value);

        var musicView = Assert.Single(views, v => v.Type == "Music");
        Assert.True(musicView.IsUserExcluded);
        Assert.False(musicView.IsScrobbled);

        var audiobooksView = Assert.Single(views, v => v.Type == "Audiobooks");
        Assert.True(audiobooksView.IsUserExcluded);
        Assert.False(audiobooksView.IsScrobbled);

        var musicVideosView = Assert.Single(views, v => v.Type == "MusicVideos");
        Assert.False(musicVideosView.IsUserExcluded);
    }

    [Fact]
    public async Task UserDevicesController_GetDevices_WhenDeviceManagerThrows_Returns503()
    {
        using var fixture = new CoreFixture();
        var deviceManager = new Mock<IDeviceManager>();
        deviceManager.Setup(m => m.GetDeviceInfos(It.IsAny<DeviceQuery>())).Throws(new InvalidOperationException("DB failed"));

        var rules = new LibraryRuleService(fixture.Store);
        var controller = new UserDevicesController(deviceManager.Object, rules);
        var httpContext = new DefaultHttpContext();
        httpContext.Items[typeof(CallerFilter)] = Api.CallerSecurityTests.Identity(fixture.UserId);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var getResult = await controller.Get(TestContext.Current.CancellationToken);
        var statusCodeResult = Assert.IsType<StatusCodeResult>(getResult.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, statusCodeResult.StatusCode);

        var putResult = await controller.Update(new UpdateUserDevicesRequest(["dev-1"]), TestContext.Current.CancellationToken);
        var putStatusResult = Assert.IsType<StatusCodeResult>(putResult.Result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, putStatusResult.StatusCode);
    }

    [Fact]
    public async Task UserDevicesController_ListsOnlyDevicesTheCallerSignedInOn()
    {
        using var fixture = new CoreFixture();
        var deviceManager = new Mock<IDeviceManager>();
        var own = new DeviceInfo { Id = "own", Name = "Phone", DateLastActivity = DateTime.UtcNow };
        var foreign = new DeviceInfo { Id = "foreign", Name = "Other user's TV", DateLastActivity = DateTime.UtcNow };
        deviceManager.Setup(m => m.GetDeviceInfos(It.Is<DeviceQuery>(q => q.UserId == fixture.UserId))).Returns(new QueryResult<DeviceInfo> { Items = [own, own], TotalRecordCount = 2 });
        deviceManager.Setup(m => m.GetDeviceInfos(It.Is<DeviceQuery>(q => q.UserId == null))).Returns(new QueryResult<DeviceInfo> { Items = [own, foreign], TotalRecordCount = 2 });
        deviceManager.Setup(m => m.GetDevicesForUser(It.IsAny<Guid?>())).Returns(new QueryResult<DeviceInfoDto> { Items = [new DeviceInfoDto { Id = "foreign", Name = "Other user's TV" }], TotalRecordCount = 1 });

        var rules = new LibraryRuleService(fixture.Store);
        var controller = new UserDevicesController(deviceManager.Object, rules);
        var httpContext = new DefaultHttpContext();
        httpContext.Items[typeof(CallerFilter)] = Api.CallerSecurityTests.Identity(fixture.UserId);
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        var list = Assert.IsAssignableFrom<IReadOnlyList<UserDeviceView>>(Assert.IsType<OkObjectResult>((await controller.Get(TestContext.Current.CancellationToken)).Result).Value);
        Assert.Equal(["own"], list.Select(d => d.Id));

        await controller.Update(new UpdateUserDevicesRequest(["foreign", "own"]), TestContext.Current.CancellationToken);
        Assert.Equal(["own"], await rules.GetUserExcludedDevicesAsync(fixture.UserId, TestContext.Current.CancellationToken));
    }
}
