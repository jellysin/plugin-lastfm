namespace JellySin.Plugin.Lastfm.Configuration;

public enum BlockRuleField
{
    Genre = 0,
    Artist = 1,
    Title = 2,
    Rating = 3
}

public sealed class BlockRule
{
    public const int MaxValueLength = 512;

    private string _value = string.Empty;

    public BlockRuleField Field { get; set; }

    public string Value
    {
        get => _value;
        set => _value = value ?? string.Empty;
    }

    public BlockRule() { }

    public BlockRule(BlockRuleField field, string value)
    {
        Field = field;
        Value = value;
    }
}
