using Jellyfin.Database.Implementations.Entities;
using JellySin.Plugin.Lastfm.Features;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Features;

public sealed class CoordinatedUserDataTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void StaleBaseItemRowsCannotReplaceTheFreshPersistedFloor()
    {
        var host = new DetachedHost();
        var stale = host.Manager.GetUserData(host.User, host.Item)!;
        host.StaleReads = true;
        host.Manager.ApplyHistoryFloor(host.User, host.Item, 100, null, Ct);
        stale.PlayCount = 4;
        host.Manager.SaveUserData(host.User, host.Item, stale, UserDataSaveReason.PlaybackFinished, Ct);
        Assert.Equal(100, host.State.PlayCount);
        host.Manager.SaveUserData(host.User, host.Item, new UpdateUserItemDataDto { IsFavorite = false }, UserDataSaveReason.UpdateUserData);
        Assert.Equal(100, host.State.PlayCount);
        Assert.False(host.State.IsFavorite);
    }

    [Fact]
    public void BatchSnapshotsCannotMutateTheHostCacheBeforeSave()
    {
        var host = new DetachedHost();
        var snapshots = host.Manager.GetUserDataBatch([host.Item], host.User);
        snapshots[host.Item.Id].PlayCount = 900;
        Assert.Equal(3, host.State.PlayCount);
    }

    [Fact]
    public void DetachedPlaybackSnapshotCannotOverwriteImportedCountOrDate()
    {
        var host = new DetachedHost();
        var playback = host.Manager.GetUserData(host.User, host.Item)!;
        var importedDate = new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc);
        host.Manager.ApplyHistoryFloor(host.User, host.Item, 100, importedDate, Ct);
        playback.PlayCount++;
        playback.PlaybackPositionTicks = 200;
        host.Manager.SaveUserData(host.User, host.Item, playback, UserDataSaveReason.PlaybackFinished, Ct);
        Assert.Equal(100, host.State.PlayCount);
        Assert.Equal(importedDate, host.State.LastPlayedDate);
        Assert.Equal(200, host.State.PlaybackPositionTicks);
        Assert.True(host.State.IsFavorite);
    }

    [Fact]
    public void FreshNativeResetAndFollowingPlaysCanReduceImportedHistory()
    {
        var host = new DetachedHost();
        host.Manager.ApplyHistoryFloor(host.User, host.Item, 100, DateTime.UtcNow, Ct);
        var reset = host.Manager.GetUserData(host.User, host.Item)!;
        reset.PlayCount = 0;
        reset.LastPlayedDate = null;
        host.Manager.SaveUserData(host.User, host.Item, reset, UserDataSaveReason.UpdateUserData, Ct);
        Assert.Equal(0, host.State.PlayCount);
        Assert.Null(host.State.LastPlayedDate);
        var playback = host.Manager.GetUserData(host.User, host.Item)!;
        playback.PlayCount++;
        host.Manager.SaveUserData(host.User, host.Item, playback, UserDataSaveReason.PlaybackFinished, Ct);
        Assert.Equal(1, host.State.PlayCount);
    }

    [Fact]
    public void NativeDtoResetInvalidatesTheOldImportFloor()
    {
        var host = new DetachedHost();
        var stale = host.Manager.GetUserData(host.User, host.Item)!;
        host.Manager.ApplyHistoryFloor(host.User, host.Item, 100, null, Ct);
        host.Manager.SaveUserData(host.User, host.Item, new UpdateUserItemDataDto { PlayCount = 0 }, UserDataSaveReason.UpdateUserData);
        stale.PlayCount = 1;
        host.Manager.SaveUserData(host.User, host.Item, stale, UserDataSaveReason.PlaybackFinished, Ct);
        Assert.Equal(1, host.State.PlayCount);
    }

    [Fact]
    public void ReplacingTheLibraryObjectDoesNotLoseSnapshotIdentity()
    {
        var host = new DetachedHost();
        var stale = host.Manager.GetUserData(host.User, host.Item)!;
        var reloaded = new Audio { Id = host.Item.Id, Name = host.Item.Name };
        host.Manager.ApplyHistoryFloor(host.User, reloaded, 90, null, Ct);
        host.Manager.SaveUserData(host.User, reloaded, stale, UserDataSaveReason.PlaybackFinished, Ct);
        Assert.Equal(90, host.State.PlayCount);
    }

    [Fact]
    public void FavouriteRemoveAndReaddInvalidatesPreviouslyReviewedRevision()
    {
        var host = new DetachedHost();
        var observed = host.Manager.GetFavouriteSnapshot(host.User, host.Item);
        host.Manager.SaveUserData(host.User, host.Item, new UpdateUserItemDataDto { IsFavorite = false }, UserDataSaveReason.UpdateUserData);
        host.Manager.SaveUserData(host.User, host.Item, new UpdateUserItemDataDto { IsFavorite = true }, UserDataSaveReason.UpdateUserData);
        Assert.False(host.Manager.TrySetFavourite(host.User, host.Item, false, observed.Revision, Ct));
        Assert.True(host.State.IsFavorite);
        var current = host.Manager.GetFavouriteSnapshot(host.User, host.Item);
        Assert.True(host.Manager.TrySetFavourite(host.User, host.Item, false, current.Revision, Ct));
        Assert.False(host.State.IsFavorite);
        Assert.Equal(3, host.State.PlayCount);
    }

    [Fact]
    public void UnknownUserDataRegistrationsRemainOwnedAndUnchanged()
    {
        var original = Mock.Of<IUserDataManager>();
        var descriptors = new[] { ServiceDescriptor.Singleton(original),
            ServiceDescriptor.Singleton<IUserDataManager>(_ => original),
            ServiceDescriptor.Scoped<IUserDataManager>(_ => original) };
        foreach (var descriptor in descriptors)
        {
            IServiceCollection services = new ServiceCollection();
            services.Add(descriptor);
            new PluginServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());
            Assert.Same(descriptor, Assert.Single(services, service => service.ServiceType == typeof(IUserDataManager)));
        }
    }

    private sealed class DetachedHost
    {
        public User User { get; } = new("Listener", "provider", "reset");
        public Audio Item { get; } = new() { Id = Guid.NewGuid(), Name = "Track" };
        public UserItemData State { get; private set; } = new() { Key = "track", PlayCount = 3, IsFavorite = true };
        public CoordinatedUserData Manager { get; }
        public bool StaleReads { get; set; }

        public DetachedHost()
        {
            var inner = new Mock<IUserDataManager>(MockBehavior.Strict);
            inner.Setup(d => d.GetUserData(User, It.IsAny<BaseItem>())).Returns(() => StaleReads
                ? new UserItemData { Key = "track", PlayCount = 3, IsFavorite = true } : Copy(State));
            inner.Setup(d => d.GetUserDataBatch(It.IsAny<IReadOnlyList<BaseItem>>(), User))
                .Returns(new Dictionary<Guid, UserItemData> { [Item.Id] = State });
            inner.Setup(d => d.SaveUserData(User, It.IsAny<BaseItem>(), It.IsAny<UserItemData>(), It.IsAny<UserDataSaveReason>(), It.IsAny<CancellationToken>()))
                .Callback<User, BaseItem, UserItemData, UserDataSaveReason, CancellationToken>((_, _, value, _, ct) => { ct.ThrowIfCancellationRequested(); State = Copy(value); });
            inner.Setup(d => d.SaveUserData(User, It.IsAny<BaseItem>(), It.IsAny<UpdateUserItemDataDto>(), It.IsAny<UserDataSaveReason>()))
                .Callback<User, BaseItem, UpdateUserItemDataDto, UserDataSaveReason>((_, _, value, _) =>
                { State.PlayCount = value.PlayCount ?? State.PlayCount; State.IsFavorite = value.IsFavorite ?? State.IsFavorite; });
            Manager = new(inner.Object, (_, _) => Copy(State));
        }

        private static UserItemData Copy(UserItemData source) => new()
        {
            Key = source.Key,
            PlayCount = source.PlayCount,
            LastPlayedDate = source.LastPlayedDate,
            IsFavorite = source.IsFavorite,
            PlaybackPositionTicks = source.PlaybackPositionTicks,
            Played = source.Played
        };
    }
}
