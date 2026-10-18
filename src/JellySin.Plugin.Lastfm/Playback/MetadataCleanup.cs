using System.Text.RegularExpressions;
using JellySin.Plugin.Lastfm.Configuration;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Playback;

public sealed class MetadataCleanup(Func<MetadataRule[]>? getRules = null, ILogger? logger = null, TimeSpan? matchTimeout = null)
{
    public const int MaxRules = 64;
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(50);

    private readonly Func<MetadataRule[]> _getRules = getRules ?? (() => []);
    private readonly ILogger? _logger = logger;
    private readonly TimeSpan _matchTimeout = matchTimeout ?? MatchTimeout;
    private readonly object _sync = new();
    private CacheEntry _cache = new([], []);

    public MetadataCleanup(MetadataRule[] rules, ILogger? logger = null)
        : this(() => rules, logger)
    {
    }

    public static MusicTrack Apply(MusicTrack track, MetadataRule[]? rules, ILogger? logger = null)
        => new MetadataCleanup(rules ?? [], logger).Apply(track);

    public MusicTrack Apply(MusicTrack track) => CleanTrack(track, _getRules());

    private MusicTrack CleanTrack(MusicTrack track, MetadataRule[]? rules)
    {
        if (rules is null || rules.Length == 0) return track;
        var compiled = GetCompiledRules(rules);
        if (compiled.Length == 0) return track;

        var artist = track.Artist;
        var title = track.Title;
        var album = track.Album;
        var albumArtist = track.AlbumArtist;

        var count = Math.Min(compiled.Length, MaxRules);
        for (var index = 0; index < count; index++)
        {
            var rule = compiled[index];
            switch (rule.Field)
            {
                case MetadataRuleField.Artist:
                    artist = CleanRequired(artist, rule);
                    break;
                case MetadataRuleField.Title:
                    title = CleanRequired(title, rule);
                    break;
                case MetadataRuleField.Album:
                    album = CleanOptional(album, rule);
                    break;
                case MetadataRuleField.AlbumArtist:
                    albumArtist = CleanOptional(albumArtist, rule);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(artist) || artist.Length > 1024) artist = track.Artist;
        if (string.IsNullOrWhiteSpace(title) || title.Length > 1024) title = track.Title;

        return track with { Artist = artist, Title = title, Album = album, AlbumArtist = albumArtist };
    }

    private string CleanRequired(string current, CompiledRule rule)
    {
        var next = ApplyRule(current, rule);
        return !string.IsNullOrWhiteSpace(next) && next.Length <= 1024 ? next : current;
    }

    private string? CleanOptional(string? current, CompiledRule rule)
    {
        if (current is null) return null;
        var next = ApplyRule(current, rule);
        return string.IsNullOrWhiteSpace(next) ? null : next;
    }

    private string ApplyRule(string value, CompiledRule rule)
    {
        if (rule.Kind == MetadataRuleKind.Correction)
            return string.Equals(value, rule.Match, StringComparison.OrdinalIgnoreCase) ? rule.Replacement : value;

        if (rule.Regex is null) return value;

        try
        {
            return rule.Regex.Replace(value, rule.Replacement);
        }
        catch (Exception exception)
        {
            _logger?.LogWarning("Metadata cleanup rule at index {RuleIndex} failed to execute ({ErrorType}).", rule.Index, exception.GetType().Name);
            return value;
        }
    }

    private CompiledRule[] GetCompiledRules(MetadataRule[] rules)
    {
        var cache = _cache;
        if (ReferenceEquals(rules, cache.Rules)) return cache.Compiled;

        lock (_sync)
        {
            if (ReferenceEquals(rules, _cache.Rules)) return _cache.Compiled;

            var count = Math.Min(rules.Length, MaxRules);
            var compiled = new CompiledRule[count];
            for (var index = 0; index < count; index++)
                compiled[index] = new CompiledRule(index, rules[index], _matchTimeout, _logger);

            _cache = new CacheEntry(rules, compiled);
            return compiled;
        }
    }

    private sealed record CacheEntry(MetadataRule[] Rules, CompiledRule[] Compiled);

    private sealed class CompiledRule
    {
        public int Index { get; }
        public MetadataRuleField Field { get; }
        public MetadataRuleKind Kind { get; }
        public string Match { get; }
        public string Replacement { get; }
        public Regex? Regex { get; }

        public CompiledRule(int index, MetadataRule rule, TimeSpan matchTimeout, ILogger? logger)
        {
            Index = index;
            Field = rule.Field;
            Kind = rule.Kind;
            Match = rule.Match;
            Replacement = rule.Replacement;
            if (rule.Kind == MetadataRuleKind.Pattern)
            {
                try
                {
                    Regex = new Regex(rule.Match, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, matchTimeout);
                }
                catch (Exception exception)
                {
                    logger?.LogWarning("Metadata cleanup rule at index {RuleIndex} could not be compiled ({ErrorType}).", index, exception.GetType().Name);
                }
            }
        }
    }
}
