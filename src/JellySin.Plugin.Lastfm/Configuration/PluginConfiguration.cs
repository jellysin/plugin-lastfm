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
    public const int MaxMetadataRules = 64;
    public const int MaxBlockRules = 64;
    private Guid[] _libraryIds = [];
    private MetadataRule[] _metadataRules = [];
    private BlockRule[] _blockRules = [];
    private int _scrobblePercent = ScrobbleThreshold.LastfmPercent;
    private int _scrobbleCeilingSeconds = ScrobbleThreshold.LastfmCeilingSeconds;
    private int _scrobbleMinimumLengthSeconds = ScrobbleThreshold.LastfmMinimumLengthSeconds;

    public bool Enabled { get; set; } = true;

    public LibraryFilterMode LibraryMode { get; set; } = LibraryFilterMode.All;

    public bool ScrobbleMusic { get; set; } = true;

    // Off by default: a chapter is not a track, and the artist Jellyfin records for it is the narrator or author.
    public bool ScrobbleAudiobooks { get; set; }

    public bool ScrobbleMusicVideos { get; set; }

    // Off by default: Last.fm files a joined credit as an artist of its own, apart from each artist's page.
    public bool ScrobbleAllArtists { get; set; }

    // Off by default: a title or artist that names its featured artist is sent as tagged, as before this option.
    public bool RemoveFeaturedArtists { get; set; }

    // Off by default: scrobble and update now playing only for tracks with a valid MusicBrainz recording id.
    public bool RequireMusicBrainzId { get; set; }

    public Guid[] LibraryIds
    {
        get => _libraryIds;
        set => _libraryIds = value is null ? [] : value.Distinct().Take(MaxConfiguredLibraries).ToArray();
    }

    public MetadataRule[] MetadataRules
    {
        get => _metadataRules;
        set => _metadataRules = value is null ? [] : value
            // An empty match would correct nothing or let a pattern insert its replacement between every character.
            .Where(rule => rule is not null && !string.IsNullOrEmpty(rule.Match)
                && rule.Match.Length <= MetadataRule.MaxMatchLength
                && (rule.Replacement?.Length ?? 0) <= MetadataRule.MaxReplacementLength)
            .Take(MaxMetadataRules)
            .ToArray();
    }

    public BlockRule[] BlockRules
    {
        get => _blockRules;
        set => _blockRules = value is null ? [] : value
            .Where(rule => rule is not null && !string.IsNullOrWhiteSpace(rule.Value)
                && rule.Value.Length <= BlockRule.MaxValueLength)
            .Take(MaxBlockRules)
            .ToArray();
    }

    // Last.fm's values are the minimum it accepts, so each setting can only make the threshold stricter.
    public int ScrobblePercent
    {
        get => _scrobblePercent;
        set => _scrobblePercent = Math.Clamp(value, ScrobbleThreshold.LastfmPercent, ScrobbleThreshold.MaxPercent);
    }

    public int ScrobbleCeilingSeconds
    {
        get => _scrobbleCeilingSeconds;
        set => _scrobbleCeilingSeconds = Math.Clamp(value, ScrobbleThreshold.LastfmCeilingSeconds, ScrobbleThreshold.MaxCeilingSeconds);
    }

    public int ScrobbleMinimumLengthSeconds
    {
        get => _scrobbleMinimumLengthSeconds;
        set => _scrobbleMinimumLengthSeconds = Math.Clamp(value, ScrobbleThreshold.LastfmMinimumLengthSeconds, ScrobbleThreshold.MaxMinimumLengthSeconds);
    }

    public ScrobbleThreshold Threshold() => new(ScrobblePercent, ScrobbleCeilingSeconds, ScrobbleMinimumLengthSeconds);
}

/// <summary>How much of a track must be heard before it counts as a listen.</summary>
/// <remarks>Last.fm counts a track longer than 30 seconds once half of it or 4 minutes, whichever is earlier, has been heard.</remarks>
public sealed record ScrobbleThreshold(int Percent, int CeilingSeconds, int MinimumLengthSeconds)
{
    public const int LastfmPercent = 50;
    public const int MaxPercent = 100;
    public const int LastfmCeilingSeconds = 240;
    public const int MaxCeilingSeconds = 7200;
    public const int LastfmMinimumLengthSeconds = 30;
    public const int MaxMinimumLengthSeconds = 600;

    public static ScrobbleThreshold Lastfm { get; } = new(LastfmPercent, LastfmCeilingSeconds, LastfmMinimumLengthSeconds);

    public bool Counts(double durationSeconds) => durationSeconds > MinimumLengthSeconds;

    public double RequiredSeconds(double durationSeconds) => Math.Min(durationSeconds * (Percent / 100.0), CeilingSeconds);
}

public sealed record ApplicationCredentials(string ApiKey, string Secret)
{
    public bool IsConfigured => ApiKey is { Length: 32 } && Secret is { Length: 32 };

    public override string ToString() => "Last.fm application credentials (redacted)";
}
