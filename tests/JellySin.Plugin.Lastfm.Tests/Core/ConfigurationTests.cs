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
    public void SettingsHoldNothingButTheMainSwitch() =>
        Assert.Equal(["Enabled"], typeof(PluginConfiguration).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly).Select(property => property.Name));
}
