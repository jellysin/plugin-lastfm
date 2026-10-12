using System.Globalization;
using System.Text.Json;
using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Tests.Core;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Tests.Features;

/// <summary>A paused favourite run names the check Last.fm's loved tracks failed, in one warning with counts only.</summary>
public sealed class FavouritesPauseLogTests
{
    private const string Artist = "Hidden Artist";
    private const string Title = "Hidden Title";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(LovedReadCheck.PageCountChanged)]
    [InlineData(LovedReadCheck.TotalChanged)]
    [InlineData(LovedReadCheck.ItemCountDiffers)]
    [InlineData(LovedReadCheck.FirstPageChanged)]
    [InlineData(LovedReadCheck.DuplicateTracks)]
    [InlineData(LovedReadCheck.EmptyPage)]
    [InlineData(LovedReadCheck.PageLimit)]
    public async Task PausedRunWritesOneWarningNamingTheFailedCheck(LovedReadCheck check)
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        await f.Favourites.SetEnabledAsync(f.Core.UserId, true, Ct);
        var calls = 0;
        f.Core.Client.Handler = (method, parameters, _) =>
        {
            Assert.Equal("user.getLovedTracks", method);
            return Scenario(check, int.Parse(parameters["page"], CultureInfo.InvariantCulture), ++calls);
        };

        var review = await f.Favourites.SyncAsync(f.Core.UserId, Ct);

        Assert.Contains("paused", review.Status!, StringComparison.Ordinal);
        var line = Assert.Single(f.FavouriteLog.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Contains($"failed the {check} check", line.Text, StringComparison.Ordinal);
        Assert.Contains($"user {Logged.User(f.Core.UserId)} paused", line.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(f.Core.UserId.ToString(), line.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Artist, line.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Title, line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompleteReadWritesNoWarning()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        f.Loved.Add(Track(1));
        var review = await f.EnableAndSyncAsync(Ct);
        Assert.DoesNotContain("paused", review.Status!, StringComparison.Ordinal);
        Assert.Empty(f.FavouriteLog.Lines);
    }

    [Fact]
    public async Task ItemCountWarningCarriesTheCounts()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        await f.Favourites.SetEnabledAsync(f.Core.UserId, true, Ct);
        f.Core.Client.Handler = (_, parameters, _) =>
            Scenario(LovedReadCheck.ItemCountDiffers, int.Parse(parameters["page"], CultureInfo.InvariantCulture), 1);

        await f.Favourites.SyncAsync(f.Core.UserId, Ct);

        var line = Assert.Single(f.FavouriteLog.Lines);
        Assert.EndsWith("after 1 of 1 pages (2 tracks reported first, 2 last, 1 read, 1 distinct).", line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirstPageWarningCountsAReorderOfTiedDates()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        await f.Favourites.SetEnabledAsync(f.Core.UserId, true, Ct);
        // Three tracks loved in the same second come back in another order: the same tracks, two positions moved.
        MusicTrack[] tied = [Track(1, 9), Track(2, 8), Track(3, 7), Track(4)];
        var calls = 0;
        f.Core.Client.Handler = (_, parameters, _) =>
            Loved(++calls == 1 ? tied : [tied[1], tied[0], tied[2], tied[3]], 1, int.Parse(parameters["page"], CultureInfo.InvariantCulture), 4);

        await f.Favourites.SyncAsync(f.Core.UserId, Ct);

        var line = Assert.Single(f.FavouriteLog.Lines);
        Assert.EndsWith("; page 1 read again: 2 of 4 positions differ, same tracks: yes, 3 of its tracks share a loved date with another.", line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirstPageWarningCountsAChangedDate()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        await f.Favourites.SetEnabledAsync(f.Core.UserId, true, Ct);
        var calls = 0;
        f.Core.Client.Handler = (_, parameters, _) =>
            Scenario(LovedReadCheck.FirstPageChanged, int.Parse(parameters["page"], CultureInfo.InvariantCulture), ++calls);

        await f.Favourites.SyncAsync(f.Core.UserId, Ct);

        var line = Assert.Single(f.FavouriteLog.Lines);
        Assert.EndsWith("; page 1 read again: 1 of 1 positions differ, same tracks: no, 0 of its tracks share a loved date with another.", line.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OtherChecksCarryNoFirstPageCounts()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        await f.Favourites.SetEnabledAsync(f.Core.UserId, true, Ct);
        f.Core.Client.Handler = (_, parameters, _) =>
            Scenario(LovedReadCheck.DuplicateTracks, int.Parse(parameters["page"], CultureInfo.InvariantCulture), 1);

        await f.Favourites.SyncAsync(f.Core.UserId, Ct);

        Assert.DoesNotContain("page 1 read again", Assert.Single(f.FavouriteLog.Lines).Text, StringComparison.Ordinal);
    }

    private static JsonDocument Scenario(LovedReadCheck check, int page, int call) => check switch
    {
        LovedReadCheck.PageCountChanged => Loved([Track(page)], page == 1 ? 2 : 3, page, 2),
        LovedReadCheck.TotalChanged => Loved([Track(page)], 2, page, page == 1 ? 2 : 3),
        // Last.fm counts a loved track the plugin cannot use, such as one without an artist name.
        LovedReadCheck.ItemCountDiffers => Loved([Track(1), new("", Title + " 2")], 1, page, 2),
        LovedReadCheck.FirstPageChanged => Loved([Track(1, call == 1 ? 0 : 60)], 1, page, 1),
        LovedReadCheck.DuplicateTracks => Loved([Track(1), Track(1)], 1, page, 2),
        LovedReadCheck.EmptyPage => Loved(page == 1 ? [Track(1)] : [], 3, page, 3),
        LovedReadCheck.PageLimit => Loved([Track(page)], 60, page, 60),
        _ => throw new ArgumentOutOfRangeException(nameof(check)),
    };

    private static JsonDocument Loved(MusicTrack[] tracks, int totalPages, int page, int total) =>
        FeatureFixture.Page("lovedtracks", tracks, totalPages, page, total);

    private static MusicTrack Track(int number, int lovedLater = 0) =>
        new(Artist, $"{Title} {number}", PlayedAt: DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + number + lovedLater));
}
