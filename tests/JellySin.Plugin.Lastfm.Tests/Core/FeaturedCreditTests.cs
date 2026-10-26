using System.Xml.Serialization;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Playback;
using MediaBrowser.Controller.Entities.Audio;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class FeaturedCreditTests
{
    [Theory]
    [InlineData("Song (feat. B)", "Song")]
    [InlineData("Song [feat. B]", "Song")]
    [InlineData("Song feat. B", "Song")]
    [InlineData("Song - feat. B", "Song")]
    [InlineData("A feat. B", "A")]
    [InlineData("A (ft. B)", "A")]
    [InlineData("Song (ft. B)", "Song")]
    [InlineData("Song (ft B)", "Song")]
    [InlineData("Song (feat B)", "Song")]
    [InlineData("Song (featuring B)", "Song")]
    [InlineData("Song [ft. B]", "Song")]
    [InlineData("Song [ft B]", "Song")]
    [InlineData("Song [feat B]", "Song")]
    [InlineData("Song [featuring B]", "Song")]
    [InlineData("Song ft. B", "Song")]
    [InlineData("Song ft B", "Song")]
    [InlineData("Song featuring B", "Song")]
    [InlineData("Song - ft. B", "Song")]
    [InlineData("Song - ft B", "Song")]
    [InlineData("Song - featuring B", "Song")]
    [InlineData("Song (FEAT. B)", "Song")]
    [InlineData("Song (Feat B)", "Song")]
    [InlineData("Song (FT. B)", "Song")]
    [InlineData("Song (FEATURING B)", "Song")]
    [InlineData("Song (Featuring B)", "Song")]
    [InlineData("Song (feat. Artist 1 & Artist 2)", "Song")]
    [InlineData("Song feat. Artist 1, Artist 2 & Artist 3", "Song")]
    [InlineData("Song (Radio Edit) (feat. B)", "Song (Radio Edit)")]
    [InlineData("Song (Radio Edit) - feat. B", "Song (Radio Edit)")]
    public void TrailingFeaturedCreditsAreStripped(string input, string expected)
        => Assert.Equal(expected, FeaturedCredit.Strip(input));

    [Theory]
    [InlineData("Aftermath")]
    [InlineData("Left Behind")]
    [InlineData("Defeat")]
    [InlineData("Software")]
    [InlineData("Feat of Strength")]
    [InlineData("Song feat. B (Live)")]
    [InlineData("Song (feat. B) (Remastered)")]
    [InlineData("Song with B")]
    [InlineData("Song (with B)")]
    [InlineData("A with B")]
    [InlineData("feat. B")]
    [InlineData("(feat. B)")]
    [InlineData("[feat. B]")]
    [InlineData("ft. B")]
    [InlineData("featuring B")]
    [InlineData("   feat. B   ")]
    public void NonTrailingAndKeywordLikeFieldsRemainUntouched(string input)
        => Assert.Equal(input, FeaturedCredit.Strip(input));

    [Fact]
    public void NewAndOldSavedSettingsKeepRemoveFeaturedArtistsDisabled()
    {
        Assert.False(new PluginConfiguration().RemoveFeaturedArtists);
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        const string saved = "<?xml version=\"1.0\"?><PluginConfiguration><Enabled>true</Enabled></PluginConfiguration>";
        using var reader = new StringReader(saved);
        Assert.False(((PluginConfiguration)serializer.Deserialize(reader)!).RemoveFeaturedArtists);
    }

    [Fact]
    public void RemoveFeaturedArtistsRoundtripsXml()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, new PluginConfiguration { RemoveFeaturedArtists = true });
        using var reader = new StringReader(writer.ToString());
        Assert.True(((PluginConfiguration)serializer.Deserialize(reader)!).RemoveFeaturedArtists);
    }

    [Fact]
    public async Task ScrobbleCarriesShortenedFeaturedCreditsWhenEnabled()
    {
        using var fixture = new CoreFixture();
        var scrobble = await ScrobbleAudioAsync(
            fixture,
            new PluginConfiguration { RemoveFeaturedArtists = true },
            album: "Album (feat. C)",
            albumArtists: ["A feat. B"]);
        Assert.Equal("A", scrobble["artist[0]"]);
        Assert.Equal("Song", scrobble["track[0]"]);
        Assert.Equal("Album (feat. C)", scrobble["album[0]"]);
        Assert.Equal("A feat. B", scrobble["albumArtist[0]"]);
    }

    [Fact]
    public async Task ScrobbleSendsUnchangedMetadataWhenDisabledByDefault()
    {
        using var fixture = new CoreFixture();
        var scrobble = await ScrobbleAudioAsync(
            fixture,
            new PluginConfiguration { RemoveFeaturedArtists = false },
            album: "Album",
            albumArtists: ["A feat. B"]);
        Assert.Equal("A feat. B", scrobble["artist[0]"]);
        Assert.Equal("Song (feat. B)", scrobble["track[0]"]);
    }

    [Fact]
    public async Task MetadataRulesExecuteAfterFeaturedCreditsAreStripped()
    {
        using var fixture = new CoreFixture();
        var config = new PluginConfiguration
        {
            RemoveFeaturedArtists = true,
            MetadataRules =
            [
                new MetadataRule(MetadataRuleField.Artist, MetadataRuleKind.Correction, "A", "Artist A"),
                new MetadataRule(MetadataRuleField.Title, MetadataRuleKind.Correction, "Song", "Corrected Song")
            ]
        };
        var scrobble = await ScrobbleAudioAsync(fixture, config);
        Assert.Equal("Artist A", scrobble["artist[0]"]);
        Assert.Equal("Corrected Song", scrobble["track[0]"]);
    }

    private static async Task<IReadOnlyDictionary<string, string>> ScrobbleAudioAsync(
        CoreFixture fixture,
        PluginConfiguration config,
        string? album = null,
        string[]? albumArtists = null)
    {
        await fixture.ConnectAsync();
        using var rules = new LibraryRuleService(fixture.Store, null, () => config);
        var outbox = new ScrobbleOutbox(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock);
        var item = new Audio
        {
            Id = Guid.NewGuid(),
            Name = "Song (feat. B)",
            Artists = ["A feat. B"],
            Album = album,
            AlbumArtists = albumArtists ?? [],
            RunTimeTicks = TimeSpan.FromSeconds(200).Ticks
        };
        await MediaTypeTests.PlayAsync(fixture, rules, outbox, item);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        return Assert.Single(fixture.Client.Calls, call => call.Method == "track.scrobble").Values;
    }
}
