using System.Text.Json;
using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Storage;
using JellySin.Plugin.Lastfm.Tests.Core;
using JellySin.Plugin.Lastfm.Transport;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Features;

public sealed class MusicViewCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PrivateViewsReuseOnlyFreshUserOwnedResponses()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        f.Recent.Add(new("Artist", "Private history"));
        var client = Cacheable(f.Core.Client, TimeSpan.FromMinutes(1));
        var api = new MusicApi(client.Object, f.Core.Accounts, f.Core.Clock, new MusicViewCache(f.Core.Store, f.Core.Clock));
        var first = await api.GetPageAsync(f.Core.UserId, "user.getRecentTracks", 1, null, Ct);
        f.Core.Clock.Advance(10);
        var second = await api.GetPageAsync(f.Core.UserId, "user.getRecentTracks", 1, null, Ct);
        Assert.Equal(first.Until, second.Until);
        Assert.Single(f.Core.Client.Calls);
        f.Core.Clock.Advance(51);
        await api.GetPageAsync(f.Core.UserId, "user.getRecentTracks", 1, null, Ct);
        Assert.Equal(2, f.Core.Client.Calls.Count);
        var other = Guid.NewGuid();
        f.Core.Client.Handler = null;
        await f.Core.ConnectAsync(other);
        f.Core.Client.Handler = f.Respond;
        f.Core.Client.Calls.Clear();
        await api.GetPageAsync(other, "user.getRecentTracks", 1, null, Ct);
        Assert.Single(f.Core.Client.Calls);
    }

    [Fact]
    public async Task ReconciliationBypassesViewsAndNoStoreResponsesAreNeverPersisted()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var client = Cacheable(f.Core.Client, TimeSpan.FromMinutes(1));
        var api = new MusicApi(client.Object, f.Core.Accounts, f.Core.Clock, new MusicViewCache(f.Core.Store, f.Core.Clock));
        await api.GetPageAsync(f.Core.UserId, "user.getLovedTracks", 1, null, Ct);
        f.Loved.Add(new("Artist", "New love"));
        var complete = await api.GetAllLovedAsync(f.Core.UserId, Ct);
        Assert.Single(complete.Tracks);
        Assert.Equal(3, f.Core.Client.Calls.Count);
        client.Setup(c => c.GetCacheLifetime(It.IsAny<JsonDocument>())).Returns(TimeSpan.Zero);
        await api.GetPageAsync(f.Core.UserId, "user.getTopTracks", 1, "7day", Ct);
        await api.GetPageAsync(f.Core.UserId, "user.getTopTracks", 1, "7day", Ct);
        Assert.Equal(5, f.Core.Client.Calls.Count);
    }

    [Fact]
    public async Task CacheQuotaFailureStillReturnsTheRetrievedViewAndDisconnectClearsIt()
    {
        using var f = new CoreFixture();
        using var small = new FileStateStore(Path.Combine(f.DirectoryPath, "cache-fixture"), 1000);
        var cache = new MusicViewCache(small, f.Clock);
        await cache.WriteAsync(f.UserId, "oversized", new string('x', 1000), TimeSpan.FromMinutes(1), Ct);
        Assert.Null(await cache.ReadAsync<string>(f.UserId, "oversized", Ct));
        await cache.WriteAsync(f.UserId, "private", "history", TimeSpan.FromHours(1), Ct);
        f.Clock.Advance(301);
        Assert.Null(await cache.ReadAsync<string>(f.UserId, "private", Ct));
        await cache.WriteAsync(f.UserId, "private", "history", TimeSpan.FromMinutes(1), Ct);
        await small.DeleteUserAsync(f.UserId, Ct);
        Assert.Null(await cache.ReadAsync<string>(f.UserId, "private", Ct));
    }

    [Fact]
    public async Task CorruptOptionalViewIsEvictedInsteadOfBlockingReload()
    {
        using var f = new CoreFixture();
        var cache = new MusicViewCache(f.Store, f.Clock);
        await cache.WriteAsync(f.UserId, "private", "history", TimeSpan.FromMinutes(1), Ct);
        var file = Assert.Single(Directory.EnumerateFiles(f.DirectoryPath, "cache-view-*.json", SearchOption.AllDirectories));
        await File.WriteAllTextAsync(file, "{", Ct);
        Assert.Null(await cache.ReadAsync<string>(f.UserId, "private", Ct));
        Assert.False(File.Exists(file));
    }

    private static Mock<ILastfmClient> Cacheable(StubClient source, TimeSpan lifetime)
    {
        var client = new Mock<ILastfmClient>();
        client.Setup(c => c.GetCacheLifetime(It.IsAny<JsonDocument>())).Returns(lifetime);
        client.Setup(c => c.CallAsync(It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>(), It.IsAny<string?>(), It.IsAny<RequestPriority>(), It.IsAny<CancellationToken>()))
            .Returns<string, IReadOnlyDictionary<string, string>, string?, RequestPriority, CancellationToken>(source.CallAsync);
        return client;
    }
}
