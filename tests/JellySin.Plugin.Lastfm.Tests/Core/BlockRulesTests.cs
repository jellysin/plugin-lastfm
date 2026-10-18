using System.Xml.Serialization;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Playback;
using MediaBrowser.Controller.Entities.Audio;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class BlockRulesTests
{
    [Fact]
    public void NewAndOldSavedSettingsHaveEmptyRules()
    {
        Assert.Empty(new PluginConfiguration().BlockRules);
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        const string saved = "<?xml version=\"1.0\"?><PluginConfiguration><Enabled>true</Enabled></PluginConfiguration>";
        using var reader = new StringReader(saved);
        Assert.Empty(((PluginConfiguration)serializer.Deserialize(reader)!).BlockRules);
    }

    [Fact]
    public void SettingRoundtripsXml()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var config = new PluginConfiguration
        {
            BlockRules =
            [
                new BlockRule(BlockRuleField.Genre, "Holiday"),
                new BlockRule(BlockRuleField.Artist, "The Beatles"),
                new BlockRule(BlockRuleField.Title, "Revolution 9"),
                new BlockRule(BlockRuleField.Rating, "Explicit")
            ]
        };
        using var writer = new StringWriter();
        serializer.Serialize(writer, config);
        using var reader = new StringReader(writer.ToString());
        var loaded = (PluginConfiguration)serializer.Deserialize(reader)!;
        Assert.Equal(4, loaded.BlockRules.Length);
        Assert.Equal(BlockRuleField.Genre, loaded.BlockRules[0].Field);
        Assert.Equal("Holiday", loaded.BlockRules[0].Value);
        Assert.Equal(BlockRuleField.Artist, loaded.BlockRules[1].Field);
        Assert.Equal("The Beatles", loaded.BlockRules[1].Value);
        Assert.Equal(BlockRuleField.Title, loaded.BlockRules[2].Field);
        Assert.Equal("Revolution 9", loaded.BlockRules[2].Value);
        Assert.Equal(BlockRuleField.Rating, loaded.BlockRules[3].Field);
        Assert.Equal("Explicit", loaded.BlockRules[3].Value);
    }

    [Fact]
    public void ConfigurationSetterEnforcesBounds()
    {
        var config = new PluginConfiguration();
        config.BlockRules = null!;
        Assert.Empty(config.BlockRules);

        var overlong = new BlockRule(BlockRuleField.Artist, new string('a', 513));
        var empty = new BlockRule(BlockRuleField.Title, "");
        var blank = new BlockRule(BlockRuleField.Genre, "   ");
        var valid = new BlockRule(BlockRuleField.Genre, "Rock");
        var boundary = new BlockRule(BlockRuleField.Rating, new string('b', 512));
        config.BlockRules = [overlong, null!, empty, blank, valid, boundary];
        Assert.Equal(2, config.BlockRules.Length);
        Assert.Equal("Rock", config.BlockRules[0].Value);
        Assert.Same(boundary, config.BlockRules[1]);

        var many = Enumerable.Range(0, 100).Select(i => new BlockRule(BlockRuleField.Title, $"Song {i}")).ToArray();
        config.BlockRules = many;
        Assert.Equal(PluginConfiguration.MaxBlockRules, config.BlockRules.Length);
    }

    [Theory]
    [InlineData("Rock", "rock", true)]
    [InlineData("Rock", " ROCK ", true)]
    [InlineData("Rock", "Rock", true)]
    [InlineData("Rock", "Rock and Roll", false)]
    [InlineData("Rock", "Hard Rock", false)]
    [InlineData("Rock", "Pop", false)]
    public void GenreMatching_CaseWhitespaceAndWholeField(string ruleValue, string itemGenre, bool expectedBlocked)
    {
        var rule = new BlockRule(BlockRuleField.Genre, ruleValue);
        var item = new Audio { Name = "Song", Genres = [itemGenre] };
        Assert.Equal(expectedBlocked, BlockRules.IsBlocked(item, [rule]));
    }

    [Theory]
    [InlineData("The Beatles", "the beatles", true)]
    [InlineData("The Beatles", " The Beatles ", true)]
    [InlineData("The Beatles", "The Beatles", true)]
    [InlineData("The Beatles", "Beatles", false)]
    [InlineData("The Beatles", "The Beatles Revival", false)]
    [InlineData("The Beatles", "Queen", false)]
    public void ArtistMatching_TrackAndAlbumArtist_CaseWhitespaceAndWholeField(string ruleValue, string artist, bool expectedBlocked)
    {
        var rule = new BlockRule(BlockRuleField.Artist, ruleValue);
        var trackArtistItem = new Audio { Name = "Song", Artists = [artist] };
        Assert.Equal(expectedBlocked, BlockRules.IsBlocked(trackArtistItem, [rule]));

        var albumArtistItem = new Audio { Name = "Song", Artists = ["Soloist"], AlbumArtists = [artist] };
        Assert.Equal(expectedBlocked, BlockRules.IsBlocked(albumArtistItem, [rule]));
    }

    [Theory]
    [InlineData("Song", "song", true)]
    [InlineData("Song", " Song ", true)]
    [InlineData("Song", "Song", true)]
    [InlineData("Song", "Song (Remix)", false)]
    [InlineData("Song", "My Song", false)]
    [InlineData("Song", "Other", false)]
    public void TitleMatching_CaseWhitespaceAndWholeField(string ruleValue, string title, bool expectedBlocked)
    {
        var rule = new BlockRule(BlockRuleField.Title, ruleValue);
        var item = new Audio { Name = title };
        Assert.Equal(expectedBlocked, BlockRules.IsBlocked(item, [rule]));
    }

    [Theory]
    [InlineData("Explicit", "explicit", true)]
    [InlineData("Explicit", " EXPLICIT ", true)]
    [InlineData("Explicit", "Explicit", true)]
    [InlineData("PG", "PG-13", false)]
    [InlineData("Explicit", "Clean", false)]
    [InlineData("Explicit", null, false)]
    public void RatingMatching_CaseWhitespaceAndWholeField(string ruleValue, string? rating, bool expectedBlocked)
    {
        var rule = new BlockRule(BlockRuleField.Rating, ruleValue);
        var item = new Audio { Name = "Song", OfficialRating = rating };
        Assert.Equal(expectedBlocked, BlockRules.IsBlocked(item, [rule]));
    }

    [Fact]
    public void EmptyAndNullValues_DoNotBlock()
    {
        Assert.False(BlockRules.IsBlocked(null, [new BlockRule(BlockRuleField.Title, "Test")]));
        var item = new Audio { Name = "Test" };
        Assert.False(BlockRules.IsBlocked(item, null));
        Assert.False(BlockRules.IsBlocked(item, []));
        Assert.False(BlockRules.IsBlocked(item, [new BlockRule(BlockRuleField.Title, "")]));
        Assert.False(BlockRules.IsBlocked(item, [new BlockRule(BlockRuleField.Title, "   ")]));
    }

    [Fact]
    public void LoopBounds_TakeLimitsAreEnforced()
    {
        var item = new Audio
        {
            Name = "Song",
            Genres = Enumerable.Range(0, 50).Select(i => $"Genre {i}").ToArray()
        };
        // Beyond MaxItemGenres (32), genre should not match
        var beyondBoundRule = new BlockRule(BlockRuleField.Genre, "Genre 40");
        Assert.False(BlockRules.IsBlocked(item, [beyondBoundRule]));

        // Within MaxItemGenres, matches
        var withinBoundRule = new BlockRule(BlockRuleField.Genre, "Genre 5");
        Assert.True(BlockRules.IsBlocked(item, [withinBoundRule]));
    }

    [Fact]
    public async Task PlaybackService_BlockedTrackProducesNoScrobble_UnblockedProducesScrobble()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var config = new PluginConfiguration
        {
            BlockRules =
            [
                new BlockRule(BlockRuleField.Genre, "Holiday"),
                new BlockRule(BlockRuleField.Artist, "Blocked Artist")
            ]
        };
        using var rules = new LibraryRuleService(fixture.Store, null, () => config);
        var outbox = new ScrobbleOutbox(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock);

        var blockedItem = new Audio
        {
            Id = Guid.NewGuid(),
            Name = "Jingle Bells",
            Artists = ["Singer"],
            Genres = ["Holiday"],
            RunTimeTicks = TimeSpan.FromSeconds(200).Ticks
        };

        var unblockedItem = new Audio
        {
            Id = Guid.NewGuid(),
            Name = "Normal Track",
            Artists = ["Allowed Artist"],
            Genres = ["Rock"],
            RunTimeTicks = TimeSpan.FromSeconds(200).Ticks
        };

        // Play blocked item
        await MediaTypeTests.PlayAsync(fixture, rules, outbox, blockedItem);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(fixture.Client.Calls, call => call.Method == "track.scrobble");

        // Play unblocked item
        await MediaTypeTests.PlayAsync(fixture, rules, outbox, unblockedItem);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        var scrobble = Assert.Single(fixture.Client.Calls, call => call.Method == "track.scrobble").Values;
        Assert.Equal("Allowed Artist", scrobble["artist[0]"]);
        Assert.Equal("Normal Track", scrobble["track[0]"]);
    }
}
