namespace JellySin.Plugin.Lastfm.Playback;

public sealed record ScrobbleLogEntry(
    Guid OccurrenceId,
    DateTimeOffset CompletedAt,
    DateTimeOffset StartedAt,
    string Artist,
    string Title,
    string? Album,
    string? AlbumArtist,
    string? MusicBrainzId,
    int ResultCode);
