using System.Xml.Serialization;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Playback;
using MediaBrowser.Controller.Entities.Audio;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class StrictMusicBrainzTests
{
    [Fact]
    public void NewAndOldSavedSettingsKeepRequireMusicBrainzIdDisabled()
    {
        Assert.False(new PluginConfiguration().RequireMusicBrainzId);
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        const string saved = "<?xml version=\"1.0\"?><PluginConfiguration><Enabled>true</Enabled></PluginConfiguration>";
        using var reader = new StringReader(saved);
        Assert.False(((PluginConfiguration)serializer.Deserialize(reader)!).RequireMusicBrainzId);
    }

    [Fact]
    public void SettingRoundtripsXml()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        var config = new PluginConfiguration { RequireMusicBrainzId = true };
        using var writer = new StringWriter();
        serializer.Serialize(writer, config);
        using var reader = new StringReader(writer.ToString());
        Assert.True(((PluginConfiguration)serializer.Deserialize(reader)!).RequireMusicBrainzId);
    }

    [Theory]
    [InlineData(true, "8f3471b5-7e6a-48da-86a9-c1c07a0f47ae", "8f3471b5-7e6a-48da-86a9-c1c07a0f47ae")]
    [InlineData(true, "8F3471B5-7E6A-48DA-86A9-C1C07A0F47AE", "8f3471b5-7e6a-48da-86a9-c1c07a0f47ae")]
    [InlineData(false, null, null)]
    [InlineData(false, "not-a-guid", null)]
    public async Task ScrobblesWhenTheOptionAllowsIt(bool required, string? providerId, string? sentId)
    {
        var scrobble = Assert.Single(await PlayAsync(required, providerId), call => call.Method == "track.scrobble").Values;
        Assert.Equal("Track", scrobble["track[0]"]);
        Assert.Equal(sentId, scrobble.GetValueOrDefault("mbid[0]"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    public async Task RequiredIdSkipsTracksWithoutAValidOne(string? providerId)
    {
        var calls = await PlayAsync(true, providerId);
        Assert.DoesNotContain(calls, call => call.Method == "track.scrobble");
    }

    private static async Task<(string Method, IReadOnlyDictionary<string, string> Values, string? Session)[]> PlayAsync(bool required, string? providerId)
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var config = new PluginConfiguration { RequireMusicBrainzId = required };
        using var rules = new LibraryRuleService(fixture.Store, null, () => config);
        var outbox = new ScrobbleOutbox(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock);
        var item = new Audio { Id = Guid.NewGuid(), Name = "Track", Artists = ["Artist"], RunTimeTicks = TimeSpan.FromSeconds(200).Ticks };
        if (providerId is not null) item.ProviderIds["MusicBrainzTrack"] = providerId;
        await MediaTypeTests.PlayAsync(fixture, rules, outbox, item);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        return fixture.Client.Calls.ToArray();
    }
}
