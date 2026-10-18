using JellySin.Plugin.Lastfm.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;

namespace JellySin.Plugin.Lastfm.Playback;

public static class BlockRules
{
    public const int MaxItemGenres = 32;
    public const int MaxItemArtists = 32;

    public static bool IsBlocked(BaseItem? item, IReadOnlyList<BlockRule>? rules)
    {
        if (item is null || rules is null || rules.Count == 0) return false;

        var ruleCount = Math.Min(rules.Count, PluginConfiguration.MaxBlockRules);
        for (var i = 0; i < ruleCount; i++)
        {
            var rule = rules[i];
            if (rule is not null && MatchesRule(item, rule))
            {
                return true;
            }
        }

        return false;
    }

    public static bool MatchesRule(BaseItem item, BlockRule rule)
    {
        var target = rule.Value?.Trim();
        if (string.IsNullOrEmpty(target)) return false;

        return rule.Field switch
        {
            BlockRuleField.Genre => MatchesAny(item.Genres?.Take(MaxItemGenres), target),
            BlockRuleField.Artist => MatchesArtist(item, target),
            BlockRuleField.Title => Matches(item.Name, target),
            BlockRuleField.Rating => Matches(item.OfficialRating, target),
            _ => false
        };
    }

    private static bool MatchesArtist(BaseItem item, string target)
    {
        if (item is IHasArtist hasArtist && MatchesAny(hasArtist.Artists?.Take(MaxItemArtists), target))
        {
            return true;
        }

        if (item is IHasAlbumArtist hasAlbumArtist && MatchesAny(hasAlbumArtist.AlbumArtists?.Take(MaxItemArtists), target))
        {
            return true;
        }

        return false;
    }

    private static bool MatchesAny(IEnumerable<string>? candidates, string target)
    {
        if (candidates is null) return false;

        foreach (var candidate in candidates)
        {
            if (Matches(candidate, target)) return true;
        }

        return false;
    }

    private static bool Matches(string? candidate, string target)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        return string.Equals(candidate.Trim(), target, StringComparison.OrdinalIgnoreCase);
    }
}
