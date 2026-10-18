using System.Xml.Serialization;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Playback;
using MediaBrowser.Controller.Entities.Audio;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class MetadataCleanupTests
{
    [Fact]
    public void NewAndOldSavedSettingsHaveEmptyRules()
    {
        Assert.Empty(new PluginConfiguration().MetadataRules);
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        const string saved = "<?xml version=\"1.0\"?><PluginConfiguration><Enabled>true</Enabled></PluginConfiguration>";
        using var reader = new StringReader(saved);
        Assert.Empty(((PluginConfiguration)serializer.Deserialize(reader)!).MetadataRules);
    }

    [Fact]
    public void SettingRoundtripsXml()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var config = new PluginConfiguration
        {
            MetadataRules =
            [
                new MetadataRule(MetadataRuleField.Artist, MetadataRuleKind.Correction, "Beatles", "The Beatles"),
                new MetadataRule(MetadataRuleField.Title, MetadataRuleKind.Pattern, @"\s+\(Remastered\)$", "")
            ]
        };
        using var writer = new StringWriter();
        serializer.Serialize(writer, config);
        using var reader = new StringReader(writer.ToString());
        var loaded = (PluginConfiguration)serializer.Deserialize(reader)!;
        Assert.Equal(2, loaded.MetadataRules.Length);
        Assert.Equal(MetadataRuleField.Artist, loaded.MetadataRules[0].Field);
        Assert.Equal(MetadataRuleKind.Correction, loaded.MetadataRules[0].Kind);
        Assert.Equal("Beatles", loaded.MetadataRules[0].Match);
        Assert.Equal("The Beatles", loaded.MetadataRules[0].Replacement);
        Assert.Equal(MetadataRuleField.Title, loaded.MetadataRules[1].Field);
        Assert.Equal(MetadataRuleKind.Pattern, loaded.MetadataRules[1].Kind);
        Assert.Equal(@"\s+\(Remastered\)$", loaded.MetadataRules[1].Match);
        Assert.Equal("", loaded.MetadataRules[1].Replacement);
    }

    [Fact]
    public void ConfigurationSetterEnforcesBounds()
    {
        var config = new PluginConfiguration();
        config.MetadataRules = null!;
        Assert.Empty(config.MetadataRules);

        var overlongMatch = new MetadataRule(MetadataRuleField.Artist, MetadataRuleKind.Correction, new string('a', 513), "ok");
        var overlongRepl = new MetadataRule(MetadataRuleField.Artist, MetadataRuleKind.Correction, "ok", new string('b', 1025));
        var valid = new MetadataRule(MetadataRuleField.Artist, MetadataRuleKind.Correction, "a", "b");
        var emptyMatch = new MetadataRule(MetadataRuleField.Title, MetadataRuleKind.Pattern, "", "x");
        var longest = new MetadataRule(MetadataRuleField.Artist, MetadataRuleKind.Pattern, new string('c', 512), new string('d', 1024));
        config.MetadataRules = [overlongMatch, overlongRepl, null!, emptyMatch, valid, longest];
        Assert.Equal(2, config.MetadataRules.Length);
        Assert.Equal("a", config.MetadataRules[0].Match);
        Assert.Same(longest, config.MetadataRules[1]);

        var many = Enumerable.Range(0, 100).Select(i => new MetadataRule(MetadataRuleField.Title, MetadataRuleKind.Correction, $"m{i}", $"r{i}")).ToArray();
        config.MetadataRules = many;
        Assert.Equal(PluginConfiguration.MaxMetadataRules, config.MetadataRules.Length);
    }

    [Fact]
    public void CorrectionRule_PositiveNegativeBoundary()
    {
        var matchBoundary = new string('x', 512);
        var replBoundary = new string('y', 1024);
        var rules = new[]
        {
            new MetadataRule(MetadataRuleField.Artist, MetadataRuleKind.Correction, "Beatles", "The Beatles"),
            new MetadataRule(MetadataRuleField.Album, MetadataRuleKind.Correction, matchBoundary, replBoundary)
        };
        var track = new MusicTrack(Guid.NewGuid(), "beatles", "Song", matchBoundary, null, 180);
        var cleaned = MetadataCleanup.Apply(track, rules);
        Assert.Equal("The Beatles", cleaned.Artist);
        Assert.Equal(replBoundary, cleaned.Album);

        var negative = new MusicTrack(Guid.NewGuid(), "Beatles Revival", "Song", "Other", null, 180);
        var cleanedNeg = MetadataCleanup.Apply(negative, rules);
        Assert.Equal("Beatles Revival", cleanedNeg.Artist);
        Assert.Equal("Other", cleanedNeg.Album);
    }

    [Fact]
    public void PatternRule_PositiveNegativeBoundary()
    {
        var rules = new[]
        {
            new MetadataRule(MetadataRuleField.Title, MetadataRuleKind.Pattern, @"\s+\(Remastered( \d{4})?\)$", ""),
            new MetadataRule(MetadataRuleField.AlbumArtist, MetadataRuleKind.Pattern, "^Various$", "Various Artists")
        };
        var track = new MusicTrack(Guid.NewGuid(), "Artist", "Song (Remastered 2020)", "Album", null, 180, "Various");
        var cleaned = MetadataCleanup.Apply(track, rules);
        Assert.Equal("Song", cleaned.Title);
        Assert.Equal("Various Artists", cleaned.AlbumArtist);

        var negative = new MusicTrack(Guid.NewGuid(), "Artist", "Song (Live)", "Album", null, 180, "Solo");
        var cleanedNeg = MetadataCleanup.Apply(negative, rules);
        Assert.Equal("Song (Live)", cleanedNeg.Title);
        Assert.Equal("Solo", cleanedNeg.AlbumArtist);
    }

    [Fact]
    public void RuleOrder_AppliesInSequence()
    {
        var rules = new[]
        {
            new MetadataRule(MetadataRuleField.Title, MetadataRuleKind.Pattern, @"\s+\(Remastered\)$", ""),
            new MetadataRule(MetadataRuleField.Title, MetadataRuleKind.Correction, "Song", "New Title")
        };
        var track = new MusicTrack(Guid.NewGuid(), "Artist", "Song (Remastered)", "Album", null, 180);
        var cleaned = MetadataCleanup.Apply(track, rules);
        Assert.Equal("New Title", cleaned.Title);
    }

    [Theory]
    [InlineData(@"(?=foo)bar")]
    [InlineData(@"(a)\1")]
    [InlineData(@"(?>abc)")]
    public void RefusedConstructs_LeaveFieldUnchanged(string refusedPattern)
    {
        var rules = new[] { new MetadataRule(MetadataRuleField.Title, MetadataRuleKind.Pattern, refusedPattern, "replaced") };
        var track = new MusicTrack(Guid.NewGuid(), "Artist", "aabar", "Album", null, 180);
        var cleaned = MetadataCleanup.Apply(track, rules);
        Assert.Equal("aabar", cleaned.Title);
    }

    [Fact]
    public void InvalidPattern_LeavesFieldUnchanged()
    {
        var rules = new[] { new MetadataRule(MetadataRuleField.Title, MetadataRuleKind.Pattern, "[unclosed", "replaced") };
        var track = new MusicTrack(Guid.NewGuid(), "Artist", "Song", "Album", null, 180);
        var cleaned = MetadataCleanup.Apply(track, rules);
        Assert.Equal("Song", cleaned.Title);
    }

    [Fact]
    public void TimedOutPattern_LeavesFieldUnchanged()
    {
        // The album has no length cap of its own, so only the timeout keeps it unchanged.
        var rules = new[] { new MetadataRule(MetadataRuleField.Album, MetadataRuleKind.Pattern, "a", "b") };
        var album = new string('a', 1_000_000);
        var track = new MusicTrack(Guid.NewGuid(), "Artist", "Title", album, null, 180);
        Assert.Equal(album, new MetadataCleanup(() => rules, null, TimeSpan.FromTicks(1)).Apply(track).Album);
        Assert.Equal(new string('b', 1_000_000), new MetadataCleanup(() => rules).Apply(track).Album);
    }

    [Fact]
    public void NeverEmptyRule_PreservesOriginalValue()
    {
        var rules = new[]
        {
            new MetadataRule(MetadataRuleField.Title, MetadataRuleKind.Pattern, ".*", ""),
            new MetadataRule(MetadataRuleField.Artist, MetadataRuleKind.Correction, "Artist", ""),
            new MetadataRule(MetadataRuleField.Album, MetadataRuleKind.Pattern, ".*", ""),
            new MetadataRule(MetadataRuleField.AlbumArtist, MetadataRuleKind.Correction, "Various", "   ")
        };
        var track = new MusicTrack(Guid.NewGuid(), "Artist", "Original Title", "Original Album", null, 180, "Various");
        var cleaned = MetadataCleanup.Apply(track, rules);
        Assert.Equal("Artist", cleaned.Artist);
        Assert.Equal("Original Title", cleaned.Title);
        Assert.Null(cleaned.Album);
        Assert.Null(cleaned.AlbumArtist);

        var overlongRules = new[]
        {
            new MetadataRule(MetadataRuleField.Title, MetadataRuleKind.Correction, "Original Title", new string('t', 1025)),
            new MetadataRule(MetadataRuleField.Artist, MetadataRuleKind.Correction, "Artist", new string('a', 1025))
        };
        var cleanedOverlong = MetadataCleanup.Apply(track, overlongRules);
        Assert.Equal("Artist", cleanedOverlong.Artist);
        Assert.Equal("Original Title", cleanedOverlong.Title);
    }

    [Fact]
    public void Caching_RebuildsOnlyWhenArrayReferenceChanges()
    {
        var ruleArray1 = new[] { new MetadataRule(MetadataRuleField.Title, MetadataRuleKind.Correction, "A", "B") };
        var ruleArray2 = new[] { new MetadataRule(MetadataRuleField.Title, MetadataRuleKind.Correction, "A", "C") };
        var current = ruleArray1;
        var cleanup = new MetadataCleanup(() => current);

        var track = new MusicTrack(Guid.NewGuid(), "Artist", "A", null, null, 180);
        var result1 = cleanup.Apply(track);
        Assert.Equal("B", result1.Title);

        var result2 = cleanup.Apply(track);
        Assert.Equal("B", result2.Title);

        current = ruleArray2;
        var result3 = cleanup.Apply(track);
        Assert.Equal("C", result3.Title);
    }

    [Fact]
    public async Task ScrobbleCarriesCleanedMetadata()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var config = new PluginConfiguration
        {
            MetadataRules =
            [
                new MetadataRule(MetadataRuleField.Artist, MetadataRuleKind.Correction, "Beatles", "The Beatles"),
                new MetadataRule(MetadataRuleField.Title, MetadataRuleKind.Pattern, @"\s+\(Remastered\)$", ""),
                new MetadataRule(MetadataRuleField.Album, MetadataRuleKind.Pattern, @"\s+\(Deluxe\)$", ""),
                new MetadataRule(MetadataRuleField.AlbumArtist, MetadataRuleKind.Correction, "Beatles", "The Beatles")
            ]
        };
        using var rules = new LibraryRuleService(fixture.Store, null, () => config);
        var outbox = new ScrobbleOutbox(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock);
        var item = new Audio
        {
            Id = Guid.NewGuid(),
            Name = "Yesterday (Remastered)",
            Artists = ["Beatles"],
            Album = "Help! (Deluxe)",
            AlbumArtists = ["Beatles"],
            RunTimeTicks = TimeSpan.FromSeconds(200).Ticks
        };
        await MediaTypeTests.PlayAsync(fixture, rules, outbox, item);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        var scrobble = Assert.Single(fixture.Client.Calls, call => call.Method == "track.scrobble").Values;
        Assert.Equal("The Beatles", scrobble["artist[0]"]);
        Assert.Equal("Yesterday", scrobble["track[0]"]);
        Assert.Equal("Help!", scrobble["album[0]"]);
    }
}
