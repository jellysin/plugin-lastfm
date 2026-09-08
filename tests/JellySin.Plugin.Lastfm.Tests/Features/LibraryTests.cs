using Jellyfin.Database.Implementations.Entities;
using JellySin.Plugin.Lastfm.Features;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Features;

public sealed class LibraryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void MatchingFiltersAccessAndPrefersAnExactIdentifier()
    {
        var user = new User("Listener", "provider", "reset");
        var item = new Audio { Id = Guid.NewGuid(), Name = "Alternate title", Artists = ["Artist"] };
        var manager = new Mock<ILibraryManager>(MockBehavior.Strict);
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(user.Id)).Returns(user);
        InternalItemsQuery? observed = null;
        manager.Setup(l => l.ConfigureUserAccess(It.IsAny<InternalItemsQuery>(), user)).Callback<InternalItemsQuery, User>((q, _) => observed = q);
        manager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([item]);
        var library = new MusicLibrary(manager.Object, users.Object, Mock.Of<IUserDataManager>());
        var id = Guid.NewGuid().ToString();
        var result = library.Match(user.Id, new MusicTrack("Artist", "Original title", MusicBrainzId: id), Ct);
        Assert.Equal(item.Id, result.ItemId);
        Assert.NotNull(observed);
        Assert.Equal(id, observed.HasAnyProviderId!["MusicBrainzTrack"]);
        manager.Verify(l => l.ConfigureUserAccess(It.IsAny<InternalItemsQuery>(), user), Times.Once);
    }

    [Fact]
    public void HistoryImportReadsCurrentStateAndOnlyRaisesTheFloor()
    {
        var user = new User("Listener", "provider", "reset");
        var item = new Audio { Id = Guid.NewGuid(), Name = "Track" };
        var later = new DateTime(2026, 9, 8, 0, 0, 0, DateTimeKind.Utc);
        var state = new UserItemData { Key = "track", PlayCount = 12, LastPlayedDate = later, IsFavorite = true, PlaybackPositionTicks = 42 };
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(user.Id)).Returns(user);
        var manager = new Mock<ILibraryManager>();
        manager.Setup(l => l.GetItemById<Audio>(item.Id, user.Id)).Returns(item);
        var data = new Mock<IUserDataManager>();
        data.Setup(d => d.GetUserData(user, item)).Returns(() => new UserItemData
        {
            Key = state.Key,
            PlayCount = state.PlayCount,
            LastPlayedDate = state.LastPlayedDate,
            IsFavorite = state.IsFavorite,
            PlaybackPositionTicks = state.PlaybackPositionTicks
        });
        data.Setup(d => d.SaveUserData(user, item, It.IsAny<UserItemData>(), UserDataSaveReason.Import, Ct))
            .Callback<User, BaseItem, UserItemData, UserDataSaveReason, CancellationToken>((_, _, value, _, _) => state = value);
        var library = new MusicLibrary(manager.Object, users.Object, new CoordinatedUserData(data.Object));
        library.ApplyHistoryFloor(user.Id, item.Id, 5, later.AddDays(-1), Ct);
        Assert.Equal(12, state.PlayCount);
        Assert.Equal(later, state.LastPlayedDate);
        Assert.True(state.IsFavorite);
        Assert.Equal(42, state.PlaybackPositionTicks);
        state.PlayCount = 20; // Playback occurred after the preview and before another application.
        library.ApplyHistoryFloor(user.Id, item.Id, 15, later.AddDays(1), Ct);
        Assert.Equal(20, state.PlayCount);
        Assert.Equal(later.AddDays(1), state.LastPlayedDate);
        data.Verify(d => d.SaveUserData(user, item, It.IsAny<UserItemData>(), UserDataSaveReason.Import, Ct), Times.Exactly(2));
    }

    [Fact]
    public void RevokedItemAccessPreventsMutations()
    {
        var library = new MusicLibrary(Mock.Of<ILibraryManager>(), Mock.Of<IUserManager>(), Mock.Of<IUserDataManager>());
        Assert.Throws<KeyNotFoundException>(() => library.SetFavourite(Guid.NewGuid(), Guid.NewGuid(), true, Ct));
        Assert.Throws<KeyNotFoundException>(() => library.ApplyHistoryFloor(Guid.NewGuid(), Guid.NewGuid(), 100, null, Ct));
    }

    [Fact]
    public void NameFallbackRejectsAmbiguityAndSearchIsBoundedToAccessibleMusic()
    {
        var user = new User("Listener", "provider", "reset");
        var first = new Audio { Id = Guid.NewGuid(), Name = "Track", Artists = ["Artist"], Album = "Album" };
        var second = new Audio { Id = Guid.NewGuid(), Name = "Track", Artists = ["Other artist"], Album = "Album" };
        var manager = new Mock<ILibraryManager>();
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(user.Id)).Returns(user);
        manager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([first, second]);
        var library = new MusicLibrary(manager.Object, users.Object, Mock.Of<IUserDataManager>());
        Assert.Equal(first.Id, library.Match(user.Id, new("Artist", "Track", "Album"), Ct).ItemId);
        second.Artists = ["Artist"];
        Assert.Equal("ambiguous", library.Match(user.Id, new("Artist", "Track", "Album"), Ct).Status);
        Assert.Equal("missing", library.Match(user.Id, new("Unknown", "Track", "Album"), Ct).Status);
        Assert.Equal(2, library.Search(user.Id, "Track", Ct).Count);
        manager.Verify(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.Limit == 20 && q.SearchTerm == "Track")), Times.Once);
        manager.Verify(l => l.ConfigureUserAccess(It.IsAny<InternalItemsQuery>(), user), Times.Exactly(4));
        Assert.Throws<ArgumentException>(() => library.Search(user.Id, "a", Ct));
        Assert.Throws<ArgumentException>(() => library.Search(user.Id, new string('x', 129), Ct));
    }

    [Fact]
    public void FavouriteReadsUseABatchAndWritesPreserveOtherUserFields()
    {
        var user = new User("Listener", "provider", "reset");
        var item = new Audio { Id = Guid.NewGuid(), Name = "Track", Artists = ["Artist"] };
        var state = new UserItemData { Key = "key", PlayCount = 5, IsFavorite = true, PlaybackPositionTicks = 100 };
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(user.Id)).Returns(user);
        var manager = new Mock<ILibraryManager>();
        manager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([item]);
        manager.Setup(l => l.GetItemById<Audio>(item.Id, user.Id)).Returns(item);
        var data = new Mock<IUserDataManager>();
        data.Setup(d => d.GetUserDataBatch(It.IsAny<IReadOnlyList<BaseItem>>(), user)).Returns(new Dictionary<Guid, UserItemData> { [item.Id] = state });
        data.Setup(d => d.GetUserData(user, item)).Returns(state);
        var library = new MusicLibrary(manager.Object, users.Object, data.Object);
        Assert.True(Assert.Single(library.GetFavourites(user.Id, Ct)).Favourite);
        Assert.Equal(5, library.Get(user.Id, item.Id, Ct).PlayCount);
        library.SetFavourite(user.Id, item.Id, false, Ct);
        library.SetFavourite(user.Id, item.Id, false, Ct);
        Assert.False(state.IsFavorite);
        Assert.Equal(100, state.PlaybackPositionTicks);
        Assert.Equal(5, state.PlayCount);
        data.Verify(d => d.SaveUserData(user, item, state, UserDataSaveReason.UpdateUserData, Ct), Times.Once);
        data.Verify(d => d.GetUserDataBatch(It.IsAny<IReadOnlyList<BaseItem>>(), user), Times.Once);
    }

    [Fact]
    public void DiscoveryEntitiesRequireMatchingArtistAndRespectSeedAccess()
    {
        var user = new User("Listener", "provider", "reset");
        var album = new MusicAlbum { Id = Guid.NewGuid(), Name = "Album", AlbumArtists = ["Artist"] };
        var artist = new MusicArtist { Id = Guid.NewGuid(), Name = "Artist" };
        var manager = new Mock<ILibraryManager>();
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(user.Id)).Returns(user);
        manager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns([album]);
        manager.Setup(l => l.GetItemById<BaseItem>(artist.Id, user.Id)).Returns(artist);
        var library = new MusicLibrary(manager.Object, users.Object, Mock.Of<IUserDataManager>());
        Assert.Equal(album.Id, library.MatchEntity(user.Id, new("album", "Album", "Artist", null, null), Ct).ItemId);
        Assert.Null(library.MatchEntity(user.Id, new("album", "Album", "Different", null, null), Ct).ItemId);
        Assert.Equal("artist", library.GetSeed(user.Id, artist.Id, Ct).Kind);
        Assert.Throws<KeyNotFoundException>(() => library.GetSeed(Guid.NewGuid(), artist.Id, Ct));
    }

    [Fact]
    public void ConflictingMusicBrainzIdentifiersNeverFallBackToTheSameTitle()
    {
        var user = new User("Listener", "provider", "reset");
        var item = new Audio { Id = Guid.NewGuid(), Name = "Same title", Artists = ["Artist"] };
        item.ProviderIds["MusicBrainzTrack"] = Guid.NewGuid().ToString();
        var manager = new Mock<ILibraryManager>();
        var users = new Mock<IUserManager>();
        users.Setup(u => u.GetUserById(user.Id)).Returns(user);
        manager.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns<InternalItemsQuery>(q => q.HasAnyProviderId is null ? [item] : []);
        var library = new MusicLibrary(manager.Object, users.Object, Mock.Of<IUserDataManager>());
        var result = library.Match(user.Id, new("Artist", "Same title", MusicBrainzId: Guid.NewGuid().ToString()), Ct);
        Assert.Null(result.ItemId);
        Assert.Equal("missing", result.Status);
    }
}
