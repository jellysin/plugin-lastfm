using System.Text.Json;
using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Tests.Features;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class RotatingPlaylistTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RetentionDaysZeroReturnsSameItemsOnSecondRefresh()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var t1 = f.AddLocal("Track 1");
        var t2 = f.AddLocal("Track 2");
        var t3 = f.AddLocal("Track 3");
        var t4 = f.AddLocal("Track 4");
        f.Top.Add(new MusicTrack("Artist", "TopSeed"));
        f.Core.Client.Handler = (method, args, session) => method == "track.getSimilar"
            ? SimilarTracksDoc("Track 1", "Track 2", "Track 3", "Track 4")
            : f.Respond(method, args, session);

        var host = new RotatingHost(f);
        var recipe = await host.Service.GenerateAsync(f.Core.UserId,
            new(Guid.Empty, "ZeroRetention", PlaylistSource.Discovery, Limit: 2, DailyRefresh: true, RetentionDays: 0), Ct);

        Assert.Equal([t1.Id, t2.Id], host.Updates[0].Ids);

        f.Core.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
        await host.Service.RefreshDueAsync(f.Core.UserId, Ct);

        Assert.Equal(2, host.Updates.Count);
        Assert.Equal([t1.Id, t2.Id], host.Updates[1].Ids);
    }

    [Fact]
    public async Task RetentionDaysSevenExcludesRecentItemsAndReturnsThemAfterEightDays()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var t1 = f.AddLocal("Track 1");
        var t2 = f.AddLocal("Track 2");
        var t3 = f.AddLocal("Track 3");
        var t4 = f.AddLocal("Track 4");
        var t5 = f.AddLocal("Track 5");
        var t6 = f.AddLocal("Track 6");
        f.Top.Add(new MusicTrack("Artist", "TopSeed"));
        f.Core.Client.Handler = (method, args, session) => method == "track.getSimilar"
            ? SimilarTracksDoc("Track 1", "Track 2", "Track 3", "Track 4", "Track 5", "Track 6")
            : f.Respond(method, args, session);

        var host = new RotatingHost(f);
        var recipe = await host.Service.GenerateAsync(f.Core.UserId,
            new(Guid.Empty, "Rotating", PlaylistSource.Discovery, Limit: 2, DailyRefresh: true, RetentionDays: 7), Ct);

        Assert.Equal([t1.Id, t2.Id], host.Updates[0].Ids);

        // Day 1: yesterday's items excluded
        f.Core.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
        await host.Service.RefreshDueAsync(f.Core.UserId, Ct);

        Assert.Equal(2, host.Updates.Count);
        Assert.Equal([t3.Id, t4.Id], host.Updates[1].Ids);

        // Day 8: 7 days after Day 1, so t1 and t2 are 8 days old and may return
        f.Core.Clock.Advance(TimeSpan.FromDays(7));
        await host.Service.RefreshDueAsync(f.Core.UserId, Ct);

        Assert.Equal(3, host.Updates.Count);
        Assert.Equal([t1.Id, t2.Id], host.Updates[2].Ids);
    }

    [Fact]
    public async Task TooFewCandidatesFillsPlaylistToLimitWithOldestUsedItems()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var t1 = f.AddLocal("Track 1");
        var t2 = f.AddLocal("Track 2");
        var t3 = f.AddLocal("Track 3");
        var t4 = f.AddLocal("Track 4");
        f.Top.Add(new MusicTrack("Artist", "TopSeed"));

        var candidates = new List<string> { "Track 1", "Track 2", "Track 3" };
        f.Core.Client.Handler = (method, args, session) => method == "track.getSimilar"
            ? SimilarTracksDoc(candidates.ToArray())
            : f.Respond(method, args, session);

        var host = new RotatingHost(f);
        var recipe = await host.Service.GenerateAsync(f.Core.UserId,
            new(Guid.Empty, "FillUp", PlaylistSource.Discovery, Limit: 3, DailyRefresh: true, RetentionDays: 7), Ct);

        Assert.Equal([t1.Id, t2.Id, t3.Id], host.Updates[0].Ids);

        // Day 1: only Track 4 is fresh; Track 1, 2, 3 were used yesterday.
        // Playlist must have Limit 3: [Track 4, Track 1, Track 2].
        candidates = ["Track 4", "Track 1", "Track 2", "Track 3"];
        f.Core.Clock.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
        await host.Service.RefreshDueAsync(f.Core.UserId, Ct);

        Assert.Equal(2, host.Updates.Count);
        Assert.Equal([t4.Id, t1.Id, t2.Id], host.Updates[1].Ids);
    }

    [Fact]
    public async Task OldestUsedCandidatesArePrioritizedWhenMultipleAgesAreExcluded()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var t1 = f.AddLocal("Track 1");
        var t2 = f.AddLocal("Track 2");
        var t3 = f.AddLocal("Track 3");
        f.Top.Add(new MusicTrack("Artist", "TopSeed"));
        f.Core.Client.Handler = (method, args, session) => method == "track.getSimilar"
            ? SimilarTracksDoc("Track 1", "Track 2", "Track 3")
            : f.Respond(method, args, session);

        var host = new RotatingHost(f);
        var recipeId = Guid.NewGuid();
        var recipe = new PlaylistRecipe(recipeId, "PrioritizeOldest", PlaylistSource.Discovery, Limit: 2, RetentionDays: 7);
        await f.Core.Store.WriteAsync(f.Core.UserId, "feature-playlists", new List<PlaylistRecipe> { recipe }, Ct);

        var now = f.Core.Clock.GetUtcNow();
        var history = new Dictionary<Guid, List<PlaylistHistoryEntry>>
        {
            [recipeId] =
            [
                new(t1.Id, now.AddDays(-6)),
                new(t2.Id, now.AddDays(-2)),
                new(t3.Id, now.AddDays(-1)),
            ]
        };
        await f.Core.Store.WriteAsync(f.Core.UserId, "playlist-history", history, Ct);

        await host.Service.GenerateAsync(f.Core.UserId, recipe, Ct);

        Assert.Single(host.Updates);
        Assert.Equal([t1.Id, t2.Id], host.Updates[0].Ids);
    }

    [Fact]
    public async Task FailedAndCancelledRefreshRecordsNoHistory()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        f.AddLocal("Track 1");
        f.Top.Add(new MusicTrack("Artist", "TopSeed"));
        f.Core.Client.Handler = (method, args, session) => method == "track.getSimilar"
            ? SimilarTracksDoc("Track 1")
            : f.Respond(method, args, session);

        var host = new RotatingHost(f) { FailUpdate = true };
        await Assert.ThrowsAsync<IOException>(() => host.Service.GenerateAsync(f.Core.UserId,
            new(Guid.Empty, "Failed", PlaylistSource.Discovery, Limit: 1, RetentionDays: 7), Ct));

        var history = await f.Core.Store.ReadAsync<Dictionary<Guid, List<PlaylistHistoryEntry>>>(f.Core.UserId, "playlist-history", Ct);
        Assert.True(history is null || history.Count == 0);

        var pending = await host.Service.GetPendingOperationAsync(f.Core.UserId, Ct);
        Assert.NotNull(pending);
        await host.Service.CancelPendingAsync(f.Core.UserId, pending.OperationId, Ct);

        history = await f.Core.Store.ReadAsync<Dictionary<Guid, List<PlaylistHistoryEntry>>>(f.Core.UserId, "playlist-history", Ct);
        Assert.True(history is null || history.Count == 0);
    }

    [Theory]
    [InlineData(-1, PlaylistSource.Discovery, false)]
    [InlineData(91, PlaylistSource.Discovery, false)]
    [InlineData(1, PlaylistSource.Loved, false)]
    [InlineData(7, PlaylistSource.Top, false)]
    [InlineData(0, PlaylistSource.Loved, true)]
    [InlineData(0, PlaylistSource.Top, true)]
    [InlineData(90, PlaylistSource.Discovery, true)]
    [InlineData(90, PlaylistSource.Similar, true)]
    public async Task ValidationEnforcesRetentionBoundsAndSourceRules(int retention, PlaylistSource source, bool valid)
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var seed = f.AddLocal("Seed");
        var top = f.AddLocal("TopTrack");
        var candidate = f.AddLocal("Candidate");
        f.Top.Add(top.Track);
        f.Loved.Add(candidate.Track);
        f.Core.Client.Handler = (method, args, session) => method == "track.getSimilar"
            ? SimilarTracksDoc("Candidate")
            : f.Respond(method, args, session);

        var host = new RotatingHost(f);
        var recipe = new PlaylistRecipe(Guid.Empty, "ValidationTest", source,
            SeedItemId: source == PlaylistSource.Similar ? seed.Id : null, Limit: 1, RetentionDays: retention);

        if (valid)
        {
            var result = await host.Service.GenerateAsync(f.Core.UserId, recipe, Ct);
            Assert.Equal(retention, result.RetentionDays);
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(() => host.Service.GenerateAsync(f.Core.UserId, recipe, Ct));
        }
    }

    [Fact]
    public void OldStoredRecipeJsonWithoutRetentionDaysLoadsAsZero()
    {
        const string json = """{"Id":"c3b1e3bb-40cb-4674-8d48-73b27cf46322","Name":"Old","Source":1,"Period":"1month","Limit":50,"DailyRefresh":false}""";
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var recipe = JsonSerializer.Deserialize<PlaylistRecipe>(json, options);
        Assert.NotNull(recipe);
        Assert.Equal(0, recipe.RetentionDays);
    }

    [Fact]
    public async Task HistoryBoundTrimsEntriesOverTwoThousandAndDropsOlderThanNinetyDays()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var track = f.AddLocal("Track");
        f.Top.Add(new MusicTrack("Artist", "TopSeed"));
        f.Core.Client.Handler = (method, args, session) => method == "track.getSimilar"
            ? SimilarTracksDoc("Track")
            : f.Respond(method, args, session);

        var recipeId = Guid.NewGuid();
        var recipe = new PlaylistRecipe(recipeId, "BoundedHistory", PlaylistSource.Discovery, Limit: 1, RetentionDays: 7);
        await f.Core.Store.WriteAsync(f.Core.UserId, "feature-playlists", new List<PlaylistRecipe> { recipe }, Ct);

        var now = f.Core.Clock.GetUtcNow();
        var entries = new List<PlaylistHistoryEntry>();

        // 10 entries older than 90 days
        for (var i = 1; i <= 10; i++)
            entries.Add(new(Guid.NewGuid(), now.AddDays(-91 - i)));

        // 2050 entries within 90 days
        for (var i = 1; i <= 2050; i++)
            entries.Add(new(Guid.NewGuid(), now.AddMinutes(-i)));

        var history = new Dictionary<Guid, List<PlaylistHistoryEntry>> { [recipeId] = entries };
        await f.Core.Store.WriteAsync(f.Core.UserId, "playlist-history", history, Ct);

        var host = new RotatingHost(f);
        await host.Service.GenerateAsync(f.Core.UserId, recipe, Ct);

        var stored = await f.Core.Store.ReadAsync<Dictionary<Guid, List<PlaylistHistoryEntry>>>(f.Core.UserId, "playlist-history", Ct);
        Assert.NotNull(stored);
        Assert.True(stored.TryGetValue(recipeId, out var savedEntries));
        Assert.Equal(2000, savedEntries.Count);
        Assert.All(savedEntries, e => Assert.True(e.LastUsedAt >= now.AddDays(-90)));
    }

    [Fact]
    public async Task DeleteRecipeRemovesItsStoredHistory()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        f.AddLocal("Track");
        f.Top.Add(new MusicTrack("Artist", "TopSeed"));
        f.Core.Client.Handler = (method, args, session) => method == "track.getSimilar"
            ? SimilarTracksDoc("Track")
            : f.Respond(method, args, session);

        var host = new RotatingHost(f);
        var recipe = await host.Service.GenerateAsync(f.Core.UserId,
            new(Guid.Empty, "ToDelete", PlaylistSource.Discovery, Limit: 1, RetentionDays: 7), Ct);

        var history = await f.Core.Store.ReadAsync<Dictionary<Guid, List<PlaylistHistoryEntry>>>(f.Core.UserId, "playlist-history", Ct);
        Assert.NotNull(history);
        Assert.True(history.ContainsKey(recipe.Id));

        await host.Service.DeleteRecipeAsync(f.Core.UserId, recipe.Id, Ct);

        history = await f.Core.Store.ReadAsync<Dictionary<Guid, List<PlaylistHistoryEntry>>>(f.Core.UserId, "playlist-history", Ct);
        Assert.True(history is null || !history.ContainsKey(recipe.Id));
    }

    private static JsonDocument SimilarTracksDoc(params string[] trackTitles)
    {
        var tracks = trackTitles.Select(title => new Dictionary<string, object>
        {
            ["name"] = title,
            ["artist"] = new Dictionary<string, string> { ["name"] = "Artist" }
        }).ToArray();
        return JsonSerializer.SerializeToDocument(new Dictionary<string, object>
        {
            ["similartracks"] = new Dictionary<string, object> { ["track"] = tracks }
        });
    }

    private sealed class RotatingHost
    {
        public PlaylistService Service { get; }
        public Playlist Playlist { get; }
        public List<PlaylistCreationRequest> Creations { get; } = [];
        public List<PlaylistUpdateRequest> Updates { get; } = [];
        public bool FailUpdate { get; set; }

        public RotatingHost(FeatureFixture f)
        {
            Playlist = new Playlist { Id = Guid.NewGuid(), OwnerUserId = f.Core.UserId, Name = "Generated", Path = "" };
            var library = new Mock<ILibraryManager>(MockBehavior.Strict);
            library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(Array.Empty<BaseItem>());
            library.Setup(l => l.GetItemById<Playlist>(Playlist.Id, f.Core.UserId)).Returns(Playlist);
            var manager = new Mock<IPlaylistManager>(MockBehavior.Strict);
            manager.Setup(m => m.CreatePlaylist(It.IsAny<PlaylistCreationRequest>())).Returns<PlaylistCreationRequest>(request =>
            {
                Creations.Add(request);
                return Task.FromResult(new PlaylistCreationResult(Playlist.Id.ToString()));
            });
            manager.Setup(m => m.UpdatePlaylist(It.IsAny<PlaylistUpdateRequest>())).Returns<PlaylistUpdateRequest>(request =>
            {
                Updates.Add(request);
                return FailUpdate ? Task.FromException(new IOException("interrupted")) : Task.CompletedTask;
            });
            var entities = new Mock<IDiscoveryLibrary>();
            entities.Setup(e => e.GetSeed(f.Core.UserId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns<Guid, Guid, CancellationToken>((_, id, _) => new MusicSeed(id, "Seed", "Artist", "track"));
            var discovery = new DiscoveryService(f.Api, f.Library, entities.Object, f.Core.Accounts);
            Service = new(f.Api, discovery, f.Library, library.Object, manager.Object, f.Core.Store, f.Locks, f.Core.Clock, f.Core.Accounts);
        }
    }
}
