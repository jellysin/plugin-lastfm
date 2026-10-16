using MediaBrowser.Model.Plugins;

namespace JellySin.Plugin.Lastfm.Configuration;

public enum LibraryFilterMode
{
    All = 0,
    AllowList = 1,
    BlockList = 2
}

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public const int MaxConfiguredLibraries = 256;
    private Guid[] _libraryIds = [];

    public bool Enabled { get; set; } = true;

    public LibraryFilterMode LibraryMode { get; set; } = LibraryFilterMode.All;

    public bool ScrobbleMusic { get; set; } = true;

    // Off by default: a chapter is not a track, and the artist Jellyfin records for it is the narrator or author.
    public bool ScrobbleAudiobooks { get; set; }

    public bool ScrobbleMusicVideos { get; set; }

    public Guid[] LibraryIds
    {
        get => _libraryIds;
        set => _libraryIds = value is null ? [] : value.Distinct().Take(MaxConfiguredLibraries).ToArray();
    }
}

public sealed record ApplicationCredentials(string ApiKey, string Secret)
{
    public bool IsConfigured => ApiKey is { Length: 32 } && Secret is { Length: 32 };

    public override string ToString() => "Last.fm application credentials (redacted)";
}
