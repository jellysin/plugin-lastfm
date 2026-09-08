using JellySin.Plugin.Lastfm.Features;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Features;

public sealed class PlaylistTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NewPlaylistIsPrivateAndRepeatedCreationReusesItsOwnedRecipe()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var local = f.AddLocal("Playlist track");
        f.Top.Add(local.Track);
        var host = new PlaylistHost(f);
        var recipe = new PlaylistRecipe(Guid.Empty, "My month", PlaylistSource.Top);
        var result = await host.Service.GenerateAsync(f.Core.UserId, recipe, Ct);
        Assert.Equal(host.Playlist.Id, result.PlaylistId);
        Assert.False(host.Creations[0].Public);
        Assert.Equal(f.Core.UserId, host.Creations[0].UserId);
        Assert.Equal([local.Id], host.Updates[0].Ids);
        var retried = await host.Service.GenerateAsync(f.Core.UserId, recipe, Ct);
        Assert.Equal(result.Id, retried.Id);
        Assert.Single(host.Creations);
    }

    [Fact]
    public async Task InterruptedUpdateRecoversTheCompleteDesiredListBeforeAcceptingNewWork()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        f.Top.Add(f.AddLocal("Recover").Track);
        var host = new PlaylistHost(f) { FailUpdate = true };
        await Assert.ThrowsAsync<IOException>(() => host.Service.GenerateAsync(f.Core.UserId, new(Guid.Empty, "Recover", PlaylistSource.Top), Ct));
        var pending = await f.Core.Store.ReadAsync<PlaylistOperation>(f.Core.UserId, "feature-playlist-operation", Ct);
        Assert.NotNull(pending);
        Assert.Single((await host.Service.GetRecipesAsync(f.Core.UserId, Ct)));
        host.FailUpdate = false;
        await host.Service.RefreshDueAsync(f.Core.UserId, Ct);
        Assert.Null(await f.Core.Store.ReadAsync<PlaylistOperation>(f.Core.UserId, "feature-playlist-operation", Ct));
        Assert.Single(host.Creations);
        Assert.Equal(host.Updates[0].Ids, host.Updates[1].Ids);
        Assert.NotNull(Assert.Single(await host.Service.GetRecipesAsync(f.Core.UserId, Ct)).UpdatedAt);
    }

    [Fact]
    public async Task CallerCannotChooseAnExistingPlaylistAndOwnershipIsRechecked()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        f.Top.Add(f.AddLocal("Owned").Track);
        var host = new PlaylistHost(f);
        var attempted = new PlaylistRecipe(Guid.Empty, "Owned", PlaylistSource.Top, PlaylistId: Guid.NewGuid());
        var result = await host.Service.GenerateAsync(f.Core.UserId, attempted, Ct);
        Assert.Equal(host.Playlist.Id, result.PlaylistId);
        host.Playlist.OwnerUserId = Guid.NewGuid();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => host.Service.GenerateAsync(f.Core.UserId, result, Ct));
        Assert.Single(host.Updates);
    }

    [Fact]
    public async Task EmptyResolutionAndForeignRecipeIdsCannotChangeTheHost()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var host = new PlaylistHost(f);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => host.Service.GenerateAsync(f.Core.UserId, new(Guid.NewGuid(), "Foreign", PlaylistSource.Top), Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Service.GenerateAsync(f.Core.UserId, new(Guid.Empty, "Empty", PlaylistSource.Top), Ct));
        Assert.Empty(host.Creations);
        Assert.Empty(host.Updates);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task InvalidRecipeLimitsFailBeforeRemoteOrHostWork(int limit)
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var host = new PlaylistHost(f);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Service.GenerateAsync(f.Core.UserId, new(Guid.Empty, "Limit", PlaylistSource.Top, Limit: limit), Ct));
        Assert.Empty(f.Core.Client.Calls);
        Assert.Empty(host.Creations);
    }

    private sealed class PlaylistHost
    {
        public PlaylistService Service { get; }
        public Playlist Playlist { get; }
        public List<PlaylistCreationRequest> Creations { get; } = [];
        public List<PlaylistUpdateRequest> Updates { get; } = [];
        public bool FailUpdate { get; set; }

        public PlaylistHost(FeatureFixture f)
        {
            Playlist = new Playlist { Id = Guid.NewGuid(), OwnerUserId = f.Core.UserId, Name = "Generated", Path = "" };
            var library = new Mock<ILibraryManager>(MockBehavior.Strict);
            library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(Array.Empty<BaseItem>());
            library.Setup(l => l.GetItemById<Playlist>(Playlist.Id, f.Core.UserId)).Returns(Playlist);
            var manager = new Mock<IPlaylistManager>(MockBehavior.Strict);
            manager.Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Returns<PlaylistCreationRequest>(request =>
            { Creations.Add(request); return Task.FromResult(new PlaylistCreationResult(Playlist.Id.ToString())); });
            manager.Setup(m => m.UpdatePlaylist(It.IsAny<PlaylistUpdateRequest>())).Returns<PlaylistUpdateRequest>(request =>
            { Updates.Add(request); return FailUpdate ? Task.FromException(new IOException("interrupted")) : Task.CompletedTask; });
            var discovery = new DiscoveryService(f.Api, f.Library, Mock.Of<IDiscoveryLibrary>(), f.Core.Accounts);
            Service = new(f.Api, discovery, f.Library, library.Object, manager.Object, f.Core.Store, f.Locks, f.Core.Clock, f.Core.Accounts);
        }
    }
}
