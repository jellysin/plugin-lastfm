using System.Globalization;
using System.Text.Json;
using JellySin.Plugin.Lastfm.Features;

namespace JellySin.Plugin.Lastfm.Tests.Features;

public sealed class HistoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PreviewDoesNotWriteAndRequiresUserOwnedUnexpiredToken()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var track = f.AddLocal("Played", playCount: 10);
        f.Top.Add(track.Track with { PlayCount = 6 });
        f.Recent.Add(track.Track with { PlayedAt = f.Core.Clock.GetUtcNow().AddDays(-1) });
        var preview = await f.History.PreviewHistoryImportAsync(f.Core.UserId, Ct);
        var entry = Assert.Single(preview.Entries);
        Assert.Equal(10, entry.ProposedPlayCount);
        Assert.Equal(f.Core.Clock.GetUtcNow().AddDays(-1).UtcDateTime, entry.ProposedLastPlayed);
        Assert.Empty(f.Library.HistoryWrites);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.History.ApplyHistoryImportAsync(Guid.NewGuid(), preview.Id, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.History.ApplyHistoryImportAsync(f.Core.UserId, Guid.NewGuid(), Ct));
        Assert.Equal(1, await f.History.ApplyHistoryImportAsync(f.Core.UserId, preview.Id, Ct));
        Assert.Single(f.Library.HistoryWrites);
        Assert.DoesNotContain(f.Core.Client.Calls, c => c.Method.StartsWith("track.", StringComparison.Ordinal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.History.ApplyHistoryImportAsync(f.Core.UserId, preview.Id, Ct));
    }

    [Fact]
    public async Task ExpiredPreviewCannotImportAndAmbiguousTracksRemainUnmatched()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var track = f.AddLocal("Ambiguous");
        f.AddLocal("Ambiguous");
        f.Top.Add(track.Track with { PlayCount = 50 });
        var preview = await f.History.PreviewHistoryImportAsync(f.Core.UserId, Ct);
        Assert.Empty(preview.Entries);
        Assert.Equal("ambiguous", Assert.Single(preview.Unmatched).Status);
        f.Core.Clock.Advance(1801);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.History.ApplyHistoryImportAsync(f.Core.UserId, preview.Id, Ct));
        Assert.Empty(f.Library.HistoryWrites);
    }

    [Fact]
    public async Task NowPlayingAndFutureTimestampsNeverBecomeLastPlayed()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var now = f.AddLocal("Now");
        var future = f.AddLocal("Future");
        f.Top.AddRange([now.Track with { PlayCount = 1 }, future.Track with { PlayCount = 1 }]);
        f.Recent.AddRange([now.Track with { PlayedAt = f.Core.Clock.GetUtcNow(), NowPlaying = true },
            future.Track with { PlayedAt = f.Core.Clock.GetUtcNow().AddDays(1) }]);
        var preview = await f.History.PreviewHistoryImportAsync(f.Core.UserId, Ct);
        Assert.All(preview.Entries, e => Assert.Null(e.ProposedLastPlayed));
    }

    [Fact]
    public async Task HistoryPagesReuseTheSameSnapshotUpperBound()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        f.Core.Client.Handler = (method, args, _) => FeatureFixture.Page("recenttracks", [], 2, int.Parse(args["page"], CultureInfo.InvariantCulture));
        var first = await f.History.GetHistoryAsync(f.Core.UserId, 1, Ct);
        f.Core.Clock.Advance(3600);
        var next = await f.History.GetHistoryAsync(f.Core.UserId, 2, Ct, first.Until);
        Assert.Equal(first.Until, next.Until);
        Assert.Equal(f.Core.Client.Calls[0].Values["to"], f.Core.Client.Calls[1].Values["to"]);
        await Assert.ThrowsAsync<ArgumentException>(() => f.History.GetHistoryAsync(f.Core.UserId, 1, Ct, f.Core.Clock.GetUtcNow().ToUnixTimeSeconds() + 1));
    }

    [Fact]
    public async Task ImportResumesAfterTenPagesWithoutStartingAgain()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        for (var index = 1; index <= 12; index++) f.AddLocal("Track " + index);
        f.Core.Client.Handler = (method, args, _) => method == "user.getRecentTracks" ? FeatureFixture.Page("recenttracks", []) :
            FeatureFixture.Page("toptracks", [new MusicTrack("Artist", "Track " + args["page"], PlayCount: 2)], 12, int.Parse(args["page"], CultureInfo.InvariantCulture));
        var first = await f.History.PreviewHistoryImportAsync(f.Core.UserId, Ct);
        Assert.False(first.Complete);
        Assert.Equal(11, first.NextPage);
        var second = await f.History.ContinueHistoryImportAsync(f.Core.UserId, first.Id, Ct);
        Assert.True(second.Complete);
        Assert.Equal(12, second.Entries.Count);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.Until, second.Until);
    }

    [Fact]
    public async Task MalformedPaginationIsNotMistakenForAnEmptyCollection()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        f.Core.Client.Handler = (_, _, _) => JsonDocument.Parse("{\"lovedtracks\":{\"track\":[]}}");
        await Assert.ThrowsAsync<InvalidDataException>(() => f.Favourites.SetEnabledAsync(f.Core.UserId, true, Ct));
        Assert.Empty(f.Library.FavouriteWrites);
    }

    [Fact]
    public async Task InterruptedPreviewCanBeRecoveredWithoutKnowingItsId()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        f.AddLocal("Resumable");
        f.Core.Client.Handler = (method, args, _) => method == "user.getRecentTracks" ? FeatureFixture.Page("recenttracks", [])
            : args["page"] == "1" ? FeatureFixture.Page("toptracks", [new MusicTrack("Artist", "Resumable", PlayCount: 10)], 2)
            : throw new IOException("Connection failed after the first page.");
        await Assert.ThrowsAsync<IOException>(() => f.History.PreviewHistoryImportAsync(f.Core.UserId, Ct));
        var checkpoint = await f.History.GetHistoryImportPreviewAsync(f.Core.UserId, Ct);
        Assert.NotNull(checkpoint);
        Assert.Equal(2, checkpoint.NextPage);
        Assert.Single(checkpoint.Entries);
        Assert.Null(await f.History.GetHistoryImportPreviewAsync(Guid.NewGuid(), Ct));
        f.Core.Clock.Advance(1801);
        Assert.Null(await f.History.GetHistoryImportPreviewAsync(f.Core.UserId, Ct));
    }
}
