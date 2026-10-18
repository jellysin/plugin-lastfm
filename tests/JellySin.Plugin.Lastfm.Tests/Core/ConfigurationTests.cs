using System.Xml.Serialization;
using JellySin.Plugin.Lastfm.Configuration;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class ConfigurationTests
{
    // Jellyfin reads a plugin's settings file with a plain XmlSerializer of the settings type and replaces a file
    // that cannot be read with the defaults, so a file of an earlier build has to load with its value intact.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SettingsFileOfAnEarlierBuildLoadsAndKeepsItsValue(bool enabled)
    {
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><PluginConfiguration xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\" "
            + "xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\"><Enabled>" + (enabled ? "true" : "false") + "</Enabled>"
            + "<MetadataEnabled>true</MetadataEnabled><SimilarityEnabled>false</SimilarityEnabled></PluginConfiguration>";
        using var reader = new StringReader(xml);
        var loaded = Assert.IsType<PluginConfiguration>(new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader));
        Assert.Equal(enabled, loaded.Enabled);
    }

    [Fact]
    public void SettingsHoldTheConfiguredProperties() =>
        Assert.Equal(["Enabled", "LibraryIds", "LibraryMode", "ScrobbleAllArtists", "ScrobbleAudiobooks", "ScrobbleCeilingSeconds", "ScrobbleMinimumLengthSeconds", "ScrobbleMusic", "ScrobbleMusicVideos", "ScrobblePercent"], typeof(PluginConfiguration).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly).Select(property => property.Name).Order());

    [Fact]
    public void SettingsWithLibraryRulesRoundtripsXml()
    {
        var libId = Guid.NewGuid();
        var config = new PluginConfiguration
        {
            Enabled = true,
            LibraryMode = LibraryFilterMode.AllowList,
            LibraryIds = [libId]
        };
        using var writer = new StringWriter();
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        serializer.Serialize(writer, config);
        using var reader = new StringReader(writer.ToString());
        var loaded = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));
        Assert.True(loaded.Enabled);
        Assert.Equal(LibraryFilterMode.AllowList, loaded.LibraryMode);
        Assert.Equal([libId], loaded.LibraryIds);
    }

    [Fact]
    public void MediaTypeSwitchesRoundtripXml()
    {
        var config = new PluginConfiguration { ScrobbleMusic = false, ScrobbleAudiobooks = true, ScrobbleMusicVideos = true };
        using var writer = new StringWriter();
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        serializer.Serialize(writer, config);
        using var reader = new StringReader(writer.ToString());
        var loaded = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));
        Assert.False(loaded.ScrobbleMusic);
        Assert.True(loaded.ScrobbleAudiobooks);
        Assert.True(loaded.ScrobbleMusicVideos);
    }

    [Fact]
    public void SettingsSavedBeforeMediaTypesExistedScrobbleMusicOnly()
    {
        const string Saved = "<?xml version=\"1.0\"?><PluginConfiguration><Enabled>true</Enabled><LibraryMode>All</LibraryMode></PluginConfiguration>";
        using var reader = new StringReader(Saved);
        var loaded = Assert.IsType<PluginConfiguration>(new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader));
        Assert.True(loaded.ScrobbleMusic);
        Assert.False(loaded.ScrobbleAudiobooks);
        Assert.False(loaded.ScrobbleMusicVideos);
    }
}
