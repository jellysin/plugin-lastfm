using System.Globalization;
using System.Text.Json;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Storage;
using JellySin.Plugin.Lastfm.Transport;

namespace JellySin.Plugin.Lastfm.Playback;

public sealed record PendingScrobble(EligibleListen Listen, int Attempts = 0, DateTimeOffset? NextAttemptAt = null, int? BlockedCode = null, int? IgnoredCode = null);
public sealed record ScrobbleReceipt(Guid OccurrenceId, int IgnoredCode, DateTimeOffset CompletedAt);
public sealed record OutboxState(List<PendingScrobble> Pending, List<ScrobbleReceipt> Receipts);
public sealed record OutboxStatus(int Pending, int Blocked, int? LastErrorCode, int? LastIgnoredCode, int Rejected, int? LastRejectedCode);

public sealed class ScrobbleOutbox(IStateStore store, AccountService accounts, ILastfmClient client, TimeProvider clock)
{
    private const int MaxPending = 10_000;
    private const int MaxReceipts = 2000;

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
            state?.Receipts.Count(item => item.IgnoredCode != 0) ?? 0, state?.Receipts.LastOrDefault(item => item.IgnoredCode != 0)?.IgnoredCode);
    }

    public async Task ResumeAsync(Guid userId, CancellationToken cancellationToken)
    {
        await store.UpdateAsync<OutboxState>(userId, "outbox", state => new OutboxState(
            (state?.Pending ?? []).Select(item => item with { BlockedCode = null, IgnoredCode = null, NextAttemptAt = null }).ToList(), state?.Receipts ?? []), cancellationToken).ConfigureAwait(false);
    }

    public async Task FlushAsync(Guid userId, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, accounts.GetOperationToken(userId));
        var token = linked.Token;
        var account = await accounts.GetAsync(userId, token).ConfigureAwait(false);
        if (account is null || account.NeedsReconnect || !account.ScrobblingEnabled) return;
        var state = await store.ReadAsync<OutboxState>(userId, "outbox", token).ConfigureAwait(false);
        var batch = state?.Pending.OrderBy(item => item.Listen.StartedAt)
            .TakeWhile(item => item.BlockedCode is null && item.IgnoredCode is null && (item.NextAttemptAt is null || item.NextAttemptAt <= clock.GetUtcNow())).Take(50).ToArray() ?? [];
        if (batch.Length == 0) return;
        var invalid = batch.Where(item => item.Listen.AccountGeneration != account.Generation || !ValidRequiredMetadata(item.Listen.Track)).ToArray();
        if (invalid.Length > 0)
        {
            await CompleteAsync(userId, invalid, invalid.Select(_ => -1).ToArray(), token).ConfigureAwait(false);
            batch = batch.Except(invalid).ToArray();
            if (batch.Length == 0) return;
        }
        try
        {
            using var response = await client.CallForApplicationAsync("track.scrobble", BuildBatch(batch), account.SessionKey, account.ApplicationIdentity, RequestPriority.Listening, token).ConfigureAwait(false);
            var codes = ReadResults(response.RootElement, batch.Length);
            await CompleteAsync(userId, batch, codes, token).ConfigureAwait(false);
        }
        catch (LastfmException exception)
        {
            if (exception.Code == 9) await accounts.MarkReconnectAsync(userId, token).ConfigureAwait(false);
            await DelayAsync(userId, batch, exception, token).ConfigureAwait(false);
        }
    }

    public async Task NowPlayingAsync(EligibleListen listen, CancellationToken cancellationToken, Func<bool>? isCurrent = null)
    {
        if (clock.GetUtcNow() - listen.StartedAt > TimeSpan.FromSeconds(20)) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, accounts.GetOperationToken(listen.UserId));
        var account = await accounts.GetAsync(listen.UserId, linked.Token).ConfigureAwait(false);
        if (account is null || account.NeedsReconnect || !account.ScrobblingEnabled || account.Generation != listen.AccountGeneration
            || accounts.GetCaptureBinding(listen.UserId)?.CaptureGeneration != listen.CaptureGeneration) return;
        try
        {
            using var response = await client.UpdateNowPlayingAsync(TrackParameters(listen.Track), account.SessionKey, account.ApplicationIdentity,
                isCurrent ?? (static () => true), linked.Token).ConfigureAwait(false);
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
        if (!string.IsNullOrWhiteSpace(track.MusicBrainzId)) values["mbid"] = track.MusicBrainzId;
        return values;
    }

    private static MusicTrack NormalizeTrack(MusicTrack track) => track with
    {
        Album = track.Album?.Length <= 4096 ? track.Album : null,
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

    private Task CompleteAsync(Guid userId, PendingScrobble[] batch, int[] codes, CancellationToken cancellationToken)
        => store.UpdateAsync<OutboxState>(userId, "outbox", current =>
        {
            var state = current ?? throw new AccountDisconnectedException();
            var identifiers = batch.Where((_, index) => codes[index] is >= -1 and <= 3).Select(item => item.Listen.OccurrenceId).ToHashSet();
            state.Pending.RemoveAll(item => identifiers.Contains(item.Listen.OccurrenceId));
            state.Receipts.AddRange(batch.Select((item, index) => new ScrobbleReceipt(item.Listen.OccurrenceId, codes[index], clock.GetUtcNow())).Where(item => identifiers.Contains(item.OccurrenceId)));
            var deferred = batch.Select((item, index) => (item.Listen.OccurrenceId, Code: codes[index])).Where(item => !identifiers.Contains(item.OccurrenceId)).ToDictionary(item => item.OccurrenceId, item => item.Code);
            state = state with { Pending = state.Pending.Select(item => deferred.TryGetValue(item.Listen.OccurrenceId, out var code) ? item with { IgnoredCode = code } : item).ToList() };
            if (state.Receipts.Count > MaxReceipts) state.Receipts.RemoveRange(0, state.Receipts.Count - MaxReceipts);
            return state;
        }, cancellationToken);

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
                    NextAttemptAt = error.Retryable ? clock.GetUtcNow().AddSeconds(Math.Min(3600, 15 * Math.Pow(2, Math.Min(item.Attempts, 8))) + Random.Shared.Next(0, 15)) : null,
                    BlockedCode = error.Retryable ? null : error.Code
                } : item).ToList()
            };
        }, cancellationToken);
}
