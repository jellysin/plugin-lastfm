using MediaBrowser.Model.Plugins;

namespace JellySin.Plugin.Lastfm.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = true;

    public bool MetadataEnabled { get; set; } = true;

    public bool SimilarityEnabled { get; set; } = true;
}

public sealed record ApplicationCredentials(string ApiKey, string Secret)
{
    public bool IsConfigured => ApiKey is { Length: 32 } && Secret is { Length: 32 };

    public override string ToString() => "Last.fm application credentials (redacted)";
}
