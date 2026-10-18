using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Storage;
using JellySin.Plugin.Lastfm.Tests.Core;
using JellySin.Plugin.Lastfm.Transport;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Features;

public sealed class FeatureWorkerLogTests
{
    [Fact]
    public async Task FailedRefreshNamesTheUserByPseudonym()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(It.IsAny<Guid>())).Throws(new InvalidOperationException("User store unavailable."));
        using var accounts = new AccountService(f.Core.Store, f.Core.Client, f.Core.Credentials, f.Core.Protection, f.Core.Clock, users.Object);
        EventHandler<UserDataSaveEventArgs>? handler = null;
        var data = new Mock<IUserDataManager>();
        data.SetupAdd(d => d.UserDataSaved += It.IsAny<EventHandler<UserDataSaveEventArgs>>())
            .Callback<EventHandler<UserDataSaveEventArgs>>(value => handler += value);
        using var log = new FakeLoggerProvider();
        using var provider = Build(f, accounts, Mock.Of<ILibraryManager>(), Mock.Of<IPlaylistManager>(), data.Object, log);
        var worker = Assert.Single(provider.GetServices<IHostedService>());
        await worker.StartAsync(ct);
        await WaitForAsync(() => handler is not null, ct);
        handler!.Invoke(null, new UserDataSaveEventArgs { UserId = f.Core.UserId, SaveReason = UserDataSaveReason.UpdateUserData });
        var line = await WarningAsync(log, "Feature refresh", ct);
        await worker.StopAsync(ct);
        Assert.Contains($"user {Logged.User(f.Core.UserId)} failed with InvalidOperationException", line, StringComparison.Ordinal);
        AssertNoRawId(log, f.Core.UserId);
    }

    [Fact]
    public async Task FailedFeatureOperationNamesTheUserByPseudonym()
    {
        var ct = TestContext.Current.CancellationToken;
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        EventHandler<UserDataSaveEventArgs>? handler = null;
        var data = new Mock<IUserDataManager>();
        data.SetupAdd(d => d.UserDataSaved += It.IsAny<EventHandler<UserDataSaveEventArgs>>())
            .Callback<EventHandler<UserDataSaveEventArgs>>(value => handler += value);
        using var log = new FakeLoggerProvider();
        using var provider = Build(f, f.Core.Accounts, Mock.Of<ILibraryManager>(), Mock.Of<IPlaylistManager>(), data.Object, log);
        var worker = Assert.Single(provider.GetServices<IHostedService>());
        await worker.StartAsync(ct);
        await WaitForAsync(() => handler is not null, ct);
        f.Runs.Dispose();
        handler!.Invoke(null, new UserDataSaveEventArgs { UserId = f.Core.UserId, SaveReason = UserDataSaveReason.UpdateUserData });
        var line = await WarningAsync(log, "A feature operation", ct);
        await worker.StopAsync(ct);
        Assert.Contains($"user {Logged.User(f.Core.UserId)} failed with ObjectDisposedException", line, StringComparison.Ordinal);
        AssertNoRawId(log, f.Core.UserId);
    }

    private static ServiceProvider Build(FeatureFixture f, AccountService accounts, ILibraryManager library,
        IPlaylistManager playlists, IUserDataManager data, FakeLoggerProvider provider)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddProvider(provider));
        services.AddLastfmFeatures();
        services.AddSingleton(accounts);
        services.AddSingleton<IStateStore>(f.Core.Store);
        services.AddSingleton<ILastfmClient>(f.Core.Client);
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton(f.Favourites);
        services.AddSingleton(f.Runs);
        services.AddSingleton<IMusicLibrary>(f.Library);
        services.AddSingleton<IMusicWriter>(f.Library);
        services.AddSingleton<IDiscoveryLibrary>(Mock.Of<IDiscoveryLibrary>());
        services.AddSingleton(library);
        services.AddSingleton(playlists);
        services.AddSingleton(data);
        return services.BuildServiceProvider();
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 500 && !condition(); attempt++) await Task.Delay(10, ct);
        Assert.True(condition(), "The worker did not reach the expected state within five seconds.");
    }

    private static async Task<string> WarningAsync(FakeLoggerProvider log, string start, CancellationToken ct)
    {
        await WaitForAsync(() => Warning(log, start) is not null, ct);
        return Warning(log, start)!;
    }

    private static void AssertNoRawId(FakeLoggerProvider log, Guid userId) =>
        Assert.DoesNotContain(Texts(log), t => t.Contains(userId.ToString(), StringComparison.OrdinalIgnoreCase)
            || t.Contains(userId.ToString("N"), StringComparison.OrdinalIgnoreCase));

    private static string? Warning(FakeLoggerProvider provider, string start) =>
        provider.Collector.GetSnapshot()
            .Where(r => r.Category is not null && r.Category.EndsWith(".FeatureWorker", StringComparison.Ordinal) && r.Level == LogLevel.Warning)
            .Select(r => r.Message)
            .FirstOrDefault(t => t.StartsWith(start, StringComparison.Ordinal));

    private static string[] Texts(FakeLoggerProvider provider) =>
        [.. provider.Collector.GetSnapshot()
            .Where(r => r.Category is not null && r.Category.EndsWith(".FeatureWorker", StringComparison.Ordinal))
            .Select(r => r.Message)];
}
