using System.Xml.Serialization;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Playback;
using MediaBrowser.Controller.Entities.Audio;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class ArtistCreditTests
{
    [Theory]
    [InlineData(false, "A")]
    [InlineData(true, "A, B & C")]
    public void SeveralArtistsAreSentFirstOrJoined(bool joined, string expected)
        => Assert.Equal(expected, ArtistCredit.Format(["A", "B", "C"], joined));

    [Fact]
    public void TwoArtistsAreJoinedWithAnAmpersand() => Assert.Equal("A & B", ArtistCredit.Format(["A", "B"], true));

    [Fact]
    public void OneArtistIsSentAsItIs() => Assert.Equal("A", ArtistCredit.Format(["A"], true));

    [Fact]
    public void BlankAndRepeatedNamesAreLeftOutOfTheCredit()
        => Assert.Equal("A & B", ArtistCredit.Format(["A", " ", " B ", "a", ""], true));

    [Fact]
    public void RepeatsOfOneArtistSendThatArtist() => Assert.Equal("A", ArtistCredit.Format(["A", "a", " "], true));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoUsableFirstArtistSendsNothing(bool joined)
    {
        Assert.Null(ArtistCredit.Format([], joined));
        Assert.Null(ArtistCredit.Format([" ", "B"], joined));
        Assert.Null(ArtistCredit.Format([new string('x', ArtistCredit.MaxLength + 1), "B"], joined));
    }

    [Fact]
    public void FirstArtistAtTheLengthLimitIsSent()
    {
        var name = new string('x', ArtistCredit.MaxLength);
        Assert.Equal(name, ArtistCredit.Format([name], false));
    }

    [Fact]
    public void CreditAtTheLengthLimitIsSentAndOneLongerFallsBackToTheFirstArtist()
    {
        var second = new string('b', ArtistCredit.MaxLength - "A & ".Length);
        Assert.Equal("A & " + second, ArtistCredit.Format(["A", second], true));
        Assert.Equal("A", ArtistCredit.Format(["A", second + "b"], true));
    }

    [Fact]
    public void MoreArtistsThanTheLimitFallBackToTheFirstArtist()
    {
        var limit = Enumerable.Range(1, ArtistCredit.MaxJoinedArtists).Select(index => $"A{index}").ToArray();
        Assert.EndsWith($"A15 & A{ArtistCredit.MaxJoinedArtists}", ArtistCredit.Format(limit, true));
        Assert.Equal("A1", ArtistCredit.Format([.. limit, "A17"], true));
    }

    [Fact]
    public void NewAndOldSavedSettingsSendTheFirstArtist()
    {
        Assert.False(new PluginConfiguration().ScrobbleAllArtists);
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        const string saved = "<?xml version=\"1.0\"?><PluginConfiguration><Enabled>true</Enabled></PluginConfiguration>";
        using var reader = new StringReader(saved);
        Assert.False(((PluginConfiguration)serializer.Deserialize(reader)!).ScrobbleAllArtists);
    }

    [Fact]
    public void SettingRoundtripsXml()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, new PluginConfiguration { ScrobbleAllArtists = true });
        using var reader = new StringReader(writer.ToString());
        Assert.True(((PluginConfiguration)serializer.Deserialize(reader)!).ScrobbleAllArtists);
    }

    // Now playing sends the same captured track (ScrobbleOutbox.TrackParameters), so the scrobble shows both.
    [Theory]
    [InlineData(false, "A")]
    [InlineData(true, "A, B & C")]
    public async Task ScrobbleCarriesTheConfiguredArtist(bool joined, string expected)
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var config = new PluginConfiguration { ScrobbleAllArtists = joined };
        using var rules = new LibraryRuleService(fixture.Store, null, () => config);
        var outbox = new ScrobbleOutbox(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock);
        var item = new Audio { Id = Guid.NewGuid(), Name = "Title", Artists = ["A", "B", "C"], AlbumArtists = ["Various Artists"], RunTimeTicks = TimeSpan.FromSeconds(200).Ticks };
        await MediaTypeTests.PlayAsync(fixture, rules, outbox, item);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        var scrobble = Assert.Single(fixture.Client.Calls, call => call.Method == "track.scrobble").Values;
        Assert.Equal(expected, scrobble["artist[0]"]);
        Assert.Equal("Various Artists", scrobble["albumArtist[0]"]);
    }
}
