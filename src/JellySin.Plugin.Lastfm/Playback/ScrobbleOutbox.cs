using System.Globalization;
using System.Text.Json;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Storage;
using JellySin.Plugin.Lastfm.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellySin.Plugin.Lastfm.Playback;

public sealed record PendingScrobble(EligibleListen Listen, int Attempts = 0, DateTimeOffset? NextAttemptAt = null, int? BlockedCode = null, int? IgnoredCode = null);
public sealed record ScrobbleReceipt(Guid OccurrenceId, int IgnoredCode, DateTimeOffset CompletedAt);
public sealed record OutboxState(List<PendingScrobble> Pending, List<ScrobbleReceipt> Receipts);
public sealed record OutboxStatus(int Pending, int Blocked, int? LastErrorCode, int? LastIgnoredCode, int Rejected, int? LastRejectedCode, int? OldestPendingDays = null);

public sealed record FlushOutcome(int Submitted, int Accepted, IReadOnlyList<int> IgnoredCodes, int? ErrorCode);
public enum NowPlayingAnswer { NotSent, Accepted, Ignored, NoIgnoreCode }
public readonly record struct NowPlayingOutcome(NowPlayingAnswer Answer, int IgnoreCode = 0);

public sealed class ScrobbleOutbox(
    IStateStore store,
    AccountService accounts,
    ILastfmClient client,
    TimeProvider clock,
    LibraryRuleService? rules = null,
    ILogger<ScrobbleOutbox>? logger = null)
{
    public const string LogStoreKey = "scrobble-log";
    public const int MaxLogEntries = 10_000;
    private readonly ILogger<ScrobbleOutbox> _logger = logger ?? NullLogger<ScrobbleOutbox>.Instance;
    private const int MaxPending = 10_000;
    private const int MaxReceipts = 2000;
    // Last.fm ignore code 4 (timestamp too new) usually means the clocks disagree; the item retries by itself after a delay.
    // Code 5 (daily limit) and unknown codes stay held until Retry.
    private const int TimestampTooNew = 4;

    public async Task EnqueueAsync(EligibleListen listen, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, accounts.GetOperationToken(listen.UserId));
        cancellationToken = linked.Token;
        var account = await accounts.GetAsync(listen.UserId, cancellationToken).ConfigureAwait(false);
        if (account is null || listen.AccountGeneration == Guid.Empty || listen.AccountGeneration != account.Generation) return;
        listen = listen with { Track = NormalizeTrack(listen.Track) };
        await store.UpdateAsync<OutboxState>(listen.UserId, "outbox", current =>
        {
            var state = current ?? new OutboxState([], []);
            if (state.Pending.Any(item => item.Listen.OccurrenceId == listen.OccurrenceId) || state.Receipts.Any(item => item.OccurrenceId == listen.OccurrenceId)) return state;
            if (state.Pending.Count >= MaxPending) throw new StorageBudgetException();
            state.Pending.Add(new PendingScrobble(listen));
            return state;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<OutboxStatus> GetStatusAsync(Guid userId, CancellationToken cancellationToken)
    {
        var state = await store.ReadAsync<OutboxState>(userId, "outbox", cancellationToken).ConfigureAwait(false);
        return new OutboxStatus(state?.Pending.Count ?? 0, state?.Pending.Count(item => item.BlockedCode.HasValue || item.IgnoredCode.HasValue) ?? 0,
            state?.Pending.LastOrDefault(item => item.BlockedCode.HasValue)?.BlockedCode,
            state?.Pending.LastOrDefault(item => item.IgnoredCode.HasValue)?.IgnoredCode,
            state?.Receipts.Count(item => item.IgnoredCode != 0) ?? 0, state?.Receipts.LastOrDefault(item => item.IgnoredCode != 0)?.IgnoredCode,
            OldestPendingDays(state));
    }

    // Whole days since the oldest play still waiting for delivery started; the page warns from its own threshold.
    private int? OldestPendingDays(OutboxState? state)
    {
        if (state is null || state.Pending.Count == 0) return null;
        var age = clock.GetUtcNow() - state.Pending.Min(item => item.Listen.StartedAt);
        return Math.Max(0, (int)Math.Floor(age.TotalDays));
    }

    public async Task ResumeAsync(Guid userId, CancellationToken cancellationToken)
    {
        await store.UpdateAsync<OutboxState>(userId, "outbox", state => new OutboxState(
            (state?.Pending ?? []).Select(item => item with { BlockedCode = null, IgnoredCode = null, NextAttemptAt = null }).ToList(), state?.Receipts ?? []), cancellationToken).ConfigureAwait(false);
    }

    public async Task<FlushOutcome?> FlushAsync(Guid userId, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, accounts.GetOperationToken(userId));
        var token = linked.Token;
        var account = await accounts.GetAsync(userId, token).ConfigureAwait(false);
        if (account is null || account.NeedsReconnect || !account.ScrobblingEnabled) return null;
        var state = await store.ReadAsync<OutboxState>(userId, "outbox", token).ConfigureAwait(false);
        var batch = state?.Pending.OrderBy(item => item.Listen.StartedAt)
            .TakeWhile(item => item.BlockedCode is null && item.IgnoredCode is null or TimestampTooNew && (item.NextAttemptAt is null || item.NextAttemptAt <= clock.GetUtcNow())).Take(50).ToArray() ?? [];
        if (batch.Length == 0) return null;
        var invalid = batch.Where(item => item.Listen.AccountGeneration != account.Generation || !ValidRequiredMetadata(item.Listen.Track)).ToArray();
        if (invalid.Length > 0)
        {
            await CompleteAsync(userId, invalid, invalid.Select(_ => -1).ToArray(), token).ConfigureAwait(false);
            batch = batch.Except(invalid).ToArray();
            if (batch.Length == 0) return null;
        }
        try
        {
            using var response = await client.CallForApplicationAsync("track.scrobble", BuildBatch(batch), account.SessionKey, account.ApplicationIdentity, RequestPriority.Listening, token).ConfigureAwait(false);
            var codes = ReadResults(response.RootElement, batch.Length);
            await CompleteAsync(userId, batch, codes, token).ConfigureAwait(false);
            await AppendLogAsync(userId, batch, codes, token).ConfigureAwait(false);
            var accepted = codes.Count(code => code == 0);
            var ignoredCodes = codes.Where(code => code != 0).Distinct().OrderBy(code => code).ToArray();
            return new FlushOutcome(batch.Length, accepted, ignoredCodes, null);
        }
        catch (LastfmException exception)
        {
            if (exception.Code == 9) await accounts.MarkReconnectAsync(userId, token).ConfigureAwait(false);
            await DelayAsync(userId, batch, exception, token).ConfigureAwait(false);
            return new FlushOutcome(batch.Length, 0, [], exception.Code);
        }
    }

    public async Task<NowPlayingOutcome> NowPlayingAsync(EligibleListen listen, CancellationToken cancellationToken, Func<bool>? isCurrent = null)
    {
        if (clock.GetUtcNow() - listen.StartedAt > TimeSpan.FromSeconds(20)) return new NowPlayingOutcome(NowPlayingAnswer.NotSent);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, accounts.GetOperationToken(listen.UserId));
        var account = await accounts.GetAsync(listen.UserId, linked.Token).ConfigureAwait(false);
        if (account is null || account.NeedsReconnect || !account.ScrobblingEnabled || account.Generation != listen.AccountGeneration
            || accounts.GetCaptureBinding(listen.UserId)?.CaptureGeneration != listen.CaptureGeneration) return new NowPlayingOutcome(NowPlayingAnswer.NotSent);
        try
        {
            using var response = await client.UpdateNowPlayingAsync(TrackParameters(listen.Track), account.SessionKey, account.ApplicationIdentity,
                isCurrent ?? (static () => true), linked.Token).ConfigureAwait(false);
            return ReadNowPlayingResult(response.RootElement);
        }
        catch (LastfmException exception) when (exception.Code == 9)
        {
            await accounts.MarkReconnectAsync(listen.UserId, linked.Token).ConfigureAwait(false);
            throw;
        }
    }

    public static Dictionary<string, string> TrackParameters(MusicTrack track)
    {
        track = NormalizeTrack(track);
        var values = new Dictionary<string, string>
        {
            ["artist"] = track.Artist,
            ["track"] = track.Title,
            ["duration"] = ((int)track.DurationSeconds).ToString(CultureInfo.InvariantCulture)
        };
        if (!string.IsNullOrWhiteSpace(track.Album)) values["album"] = track.Album;
        if (track.AlbumArtist is not null) values["albumArtist"] = track.AlbumArtist;
        if (!string.IsNullOrWhiteSpace(track.MusicBrainzId)) values["mbid"] = track.MusicBrainzId;
        return values;
    }

    private static MusicTrack NormalizeTrack(MusicTrack track) => track with
    {
        Album = track.Album?.Length <= 4096 ? track.Album : null,
        // Last.fm takes the album artist only when it differs from the track artist.
        AlbumArtist = !string.IsNullOrWhiteSpace(track.AlbumArtist) && track.AlbumArtist.Length <= 1024
            && !string.Equals(track.AlbumArtist, track.Artist, StringComparison.Ordinal) ? track.AlbumArtist : null,
        MusicBrainzId = Guid.TryParse(track.MusicBrainzId, out var id) ? id.ToString() : null
    };

    private static bool ValidRequiredMetadata(MusicTrack track) => !string.IsNullOrWhiteSpace(track.Artist) && track.Artist.Length <= 1024
        && !string.IsNullOrWhiteSpace(track.Title) && track.Title.Length <= 1024 && double.IsFinite(track.DurationSeconds)
        && track.DurationSeconds is > 30 and <= int.MaxValue;

    private static Dictionary<string, string> BuildBatch(PendingScrobble[] batch)
    {
        var values = new Dictionary<string, string>();
        for (var index = 0; index < batch.Length; index++)
        {
            foreach (var pair in TrackParameters(batch[index].Listen.Track)) values[$"{pair.Key}[{index}]"] = pair.Value;
            values[$"timestamp[{index}]"] = batch[index].Listen.StartedAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        }
        return values;
    }

    private static int[] ReadResults(JsonElement root, int expected)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("scrobbles", out var scrobbles)
            || scrobbles.ValueKind != JsonValueKind.Object || !scrobbles.TryGetProperty("scrobble", out var entries)) throw new LastfmException(16);
        var elements = entries.ValueKind == JsonValueKind.Array ? entries.EnumerateArray().ToArray() : [entries];
        if (elements.Length != expected) throw new LastfmException(16);
        return elements.Select(entry =>
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("ignoredMessage", out var message)
                || message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("code", out var code)) throw new LastfmException(16);
            return code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var numeric) ? numeric
                : code.ValueKind == JsonValueKind.String && int.TryParse(code.GetString(), out var parsed) ? parsed : throw new LastfmException(16);
        }).ToArray();
    }

    private static NowPlayingOutcome ReadNowPlayingResult(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("nowplaying", out var nowPlaying)
            && nowPlaying.ValueKind == JsonValueKind.Object
            && nowPlaying.TryGetProperty("ignoredMessage", out var message)
            && message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("code", out var code))
        {
            if (code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var numeric))
                return numeric == 0 ? new NowPlayingOutcome(NowPlayingAnswer.Accepted, 0) : new NowPlayingOutcome(NowPlayingAnswer.Ignored, numeric);
            if (code.ValueKind == JsonValueKind.String && int.TryParse(code.GetString(), out var parsed))
                return parsed == 0 ? new NowPlayingOutcome(NowPlayingAnswer.Accepted, 0) : new NowPlayingOutcome(NowPlayingAnswer.Ignored, parsed);
        }
        return new NowPlayingOutcome(NowPlayingAnswer.NoIgnoreCode);
    }

    private Task CompleteAsync(Guid userId, PendingScrobble[] batch, int[] codes, CancellationToken cancellationToken)
        => store.UpdateAsync<OutboxState>(userId, "outbox", current =>
        {
            var state = current ?? throw new AccountDisconnectedException();
            var identifiers = batch.Where((_, index) => codes[index] is >= -1 and <= 3).Select(item => item.Listen.OccurrenceId).ToHashSet();
            state.Pending.RemoveAll(item => identifiers.Contains(item.Listen.OccurrenceId));
            state.Receipts.AddRange(batch.Select((item, index) => new ScrobbleReceipt(item.Listen.OccurrenceId, codes[index], clock.GetUtcNow())).Where(item => identifiers.Contains(item.OccurrenceId)));
            var deferred = batch.Select((item, index) => (item.Listen.OccurrenceId, Code: codes[index])).Where(item => !identifiers.Contains(item.OccurrenceId)).ToDictionary(item => item.OccurrenceId, item => item.Code);
            state = state with { Pending = state.Pending.Select(item => deferred.TryGetValue(item.Listen.OccurrenceId, out var code) ? Hold(item, code) : item).ToList() };
            if (state.Receipts.Count > MaxReceipts) state.Receipts.RemoveRange(0, state.Receipts.Count - MaxReceipts);
            return state;
        }, cancellationToken);

    private PendingScrobble Hold(PendingScrobble item, int code) => code == TimestampTooNew
        ? item with { IgnoredCode = code, Attempts = Math.Min(item.Attempts + 1, 20), NextAttemptAt = RetryAt(item.Attempts) }
        : item with { IgnoredCode = code };

    private DateTimeOffset RetryAt(int attempts) => clock.GetUtcNow().AddSeconds(Math.Min(3600, 15 * Math.Pow(2, Math.Min(attempts, 8))) + Random.Shared.Next(0, 15));

    private Task DelayAsync(Guid userId, PendingScrobble[] batch, LastfmException error, CancellationToken cancellationToken)
        => store.UpdateAsync<OutboxState>(userId, "outbox", current =>
        {
            var state = current ?? throw new AccountDisconnectedException();
            var identifiers = batch.Select(item => item.Listen.OccurrenceId).ToHashSet();
            return state with
            {
                Pending = state.Pending.Select(item => identifiers.Contains(item.Listen.OccurrenceId)
                ? item with
                {
                    Attempts = Math.Min(item.Attempts + 1, 20),
                    NextAttemptAt = error.Retryable ? RetryAt(item.Attempts) : null,
                    BlockedCode = error.Retryable ? null : error.Code
                } : item).ToList()
            };
        }, cancellationToken);

    private async Task AppendLogAsync(Guid userId, PendingScrobble[] batch, int[] codes, CancellationToken cancellationToken)
    {
        if (rules is null) return;
        try
        {
            if (!await rules.IsScrobbleLogEnabledAsync(userId, cancellationToken).ConfigureAwait(false)) return;
            var entries = ExtractCompletedEntries(batch, codes);
            if (entries.Count == 0) return;
            await store.UpdateAsync<List<ScrobbleLogEntry>>(userId, LogStoreKey, current => MergeLogEntries(current, entries), cancellationToken).ConfigureAwait(false);
            // Switching the log off between the check and the write must not leave a log behind.
            if (!await rules.IsScrobbleLogEnabledAsync(userId, cancellationToken).ConfigureAwait(false))
                await store.DeleteAsync(userId, LogStoreKey, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning("Failed to append scrobble log ({ErrorType}).", exception.GetType().Name);
        }
    }

    private List<ScrobbleLogEntry> ExtractCompletedEntries(PendingScrobble[] batch, int[] codes)
    {
        var entries = new List<ScrobbleLogEntry>();
        var now = clock.GetUtcNow();
        for (var i = 0; i < batch.Length; i++)
        {
            var code = codes[i];
            if (code is >= 0 and <= 3)
            {
                var track = batch[i].Listen.Track;
                entries.Add(new ScrobbleLogEntry(
                    batch[i].Listen.OccurrenceId,
                    now,
                    batch[i].Listen.StartedAt,
                    track.Artist,
                    track.Title,
                    track.Album,
                    track.AlbumArtist,
                    track.MusicBrainzId,
                    code));
            }
        }
        return entries;
    }

    private static List<ScrobbleLogEntry> MergeLogEntries(List<ScrobbleLogEntry>? current, List<ScrobbleLogEntry> entries)
    {
        var log = current ?? [];
        var existing = log.Select(e => e.OccurrenceId).ToHashSet();
        foreach (var entry in entries)
        {
            if (existing.Add(entry.OccurrenceId))
            {
                log.Add(entry);
            }
        }
        if (log.Count > MaxLogEntries)
        {
            log.RemoveRange(0, log.Count - MaxLogEntries);
        }
        return log;
    }
}
