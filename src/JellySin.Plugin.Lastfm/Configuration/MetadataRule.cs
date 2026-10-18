namespace JellySin.Plugin.Lastfm.Configuration;

public enum MetadataRuleField
{
    Artist = 0,
    Title = 1,
    Album = 2,
    AlbumArtist = 3
}

public enum MetadataRuleKind
{
    Correction = 0,
    Pattern = 1
}

public sealed class MetadataRule
{
    public const int MaxMatchLength = 512;
    public const int MaxReplacementLength = 1024;

    private string _match = string.Empty;
    private string _replacement = string.Empty;

    public MetadataRuleField Field { get; set; }

    public MetadataRuleKind Kind { get; set; }

    public string Match
    {
        get => _match;
        set => _match = value ?? string.Empty;
    }

    public string Replacement
    {
        get => _replacement;
        set => _replacement = value ?? string.Empty;
    }

    public MetadataRule() { }

    public MetadataRule(MetadataRuleField field, MetadataRuleKind kind, string match, string replacement)
    {
        Field = field;
        Kind = kind;
        Match = match;
        Replacement = replacement;
    }
}
