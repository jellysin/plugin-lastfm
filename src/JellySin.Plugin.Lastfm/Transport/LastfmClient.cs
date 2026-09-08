using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Transport;

/// <summary>One bounded scheduler and throttle for all plugin API traffic. Callers own returned JSON documents.</summary>
public sealed class LastfmClient(HttpClient http, ApplicationCredentialService credentials, IStateStore store, TimeProvider clock, ILogger<LastfmClient>? logger = null) : BackgroundService, ILastfmClient
{
    private const int MaxResponseBytes = 2_000_000;
    private readonly Channel<Work>[] _queues = [NewQueue(), NewQueue(), NewQueue()];
    private readonly SemaphoreSlim _available = new(0);
    private DateTimeOffset _throttledUntil;
    private string? _suspendedKey;
    private int _dispatchCount;
    private readonly ConditionalWeakTable<JsonDocument, ResponsePolicy> _responsePolicies = new();

    public bool CanPersist(JsonDocument response) => !_responsePolicies.TryGetValue(response, out var policy) || !policy.NoStore;

    public TimeSpan GetCacheLifetime(JsonDocument response) => _responsePolicies.TryGetValue(response, out var policy)
        && policy.ExpiresAt is { } expires && expires > clock.GetUtcNow() ? expires - clock.GetUtcNow() : TimeSpan.Zero;

    public Task<JsonDocument> CallAsync(string method, IReadOnlyDictionary<string, string> parameters, string? sessionKey, RequestPriority priority, CancellationToken cancellationToken)
        => QueueAsync(method, parameters, sessionKey, null, priority, null, cancellationToken);

    public Task<JsonDocument> CallForApplicationAsync(string method, IReadOnlyDictionary<string, string> parameters, string? sessionKey,
        string applicationIdentity, RequestPriority priority, CancellationToken cancellationToken)
        => QueueAsync(method, parameters, sessionKey, applicationIdentity, priority, null, cancellationToken);

    public Task<JsonDocument> UpdateNowPlayingAsync(IReadOnlyDictionary<string, string> parameters, string sessionKey,
        string applicationIdentity, Func<bool> isCurrent, CancellationToken cancellationToken)
        => QueueAsync("track.updateNowPlaying", parameters, sessionKey, applicationIdentity, RequestPriority.Listening, isCurrent, cancellationToken);

    private async Task<JsonDocument> QueueAsync(string method, IReadOnlyDictionary<string, string> parameters, string? sessionKey,
        string? applicationIdentity, RequestPriority priority, Func<bool>? isCurrent, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (method.Length > 80 || parameters.Count > 500 || parameters.Any(pair => pair.Key.Length > 80 || pair.Value.Length > 4096))
            throw new ArgumentException("Last.fm request exceeds supported bounds.", nameof(parameters));
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        cancellationToken.ThrowIfCancellationRequested();
        var work = new Work(method, new Dictionary<string, string>(parameters), sessionKey, applicationIdentity, clock.GetUtcNow(), cancellationToken, isCurrent);
        if (!_queues[(int)priority].Writer.TryWrite(work)) throw new LastfmException(16);
        _available.Release();
        using var registration = cancellationToken.Register(() => work.Completion.TrySetCanceled(cancellationToken));
        return await work.Completion.Task.ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            try
            {
                if (!(await credentials.GetAsync(stoppingToken).ConfigureAwait(false)).IsConfigured)
                    logger?.LogWarning("JellySin Last.fm is waiting for application credentials. Account connection is unavailable until configured.");
                else logger?.LogInformation("JellySin Last.fm application credentials are configured. Individual accounts connect through Last.fm.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { logger?.LogWarning("JellySin Last.fm application settings could not be read ({ErrorType}).", exception.GetType().Name); }
            while (!stoppingToken.IsCancellationRequested)
            {
                await _available.WaitAsync(stoppingToken).ConfigureAwait(false);
                var work = TakeNext();
                if (work is null || work.CancellationToken.IsCancellationRequested) continue;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, work.CancellationToken);
                linked.CancelAfter(TimeSpan.FromSeconds(25));
                try
                {
                    var result = await SendAsync(work, linked.Token).ConfigureAwait(false);
                    if (!work.Completion.TrySetResult(result)) result.Dispose();
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || work.CancellationToken.IsCancellationRequested)
                { work.Completion.TrySetCanceled(linked.Token); }
                catch (OperationCanceledException) { work.Completion.TrySetException(new LastfmException(16)); }
                catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
                { work.Completion.TrySetException(new LastfmException(16)); }
                catch (Exception exception) { work.Completion.TrySetException(exception); }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            CancelPending(stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try { await base.StopAsync(cancellationToken).ConfigureAwait(false); }
        finally { CancelPending(cancellationToken); }
    }

    private void CancelPending(CancellationToken cancellationToken)
    {
        foreach (var queue in _queues)
        {
            queue.Writer.TryComplete();
            while (queue.Reader.TryRead(out var pending)) pending.Completion.TrySetCanceled(cancellationToken);
        }
    }

    private Work? TakeNext()
    {
        _dispatchCount = (_dispatchCount + 1) % 16;
        if (_dispatchCount == 0 && _queues[2].Reader.TryRead(out var background)) return background;
        if (_dispatchCount % 4 == 0 && _queues[1].Reader.TryRead(out var interactive)) return interactive;
        foreach (var queue in _queues)
            if (queue.Reader.TryRead(out var work)) return work;
        return null;
    }

    private async Task<JsonDocument> SendAsync(Work work, CancellationToken cancellationToken)
    {
        if (work.Method == "track.updateNowPlaying" && clock.GetUtcNow() - work.CreatedAt > TimeSpan.FromSeconds(20)) throw new LastfmException(8);
        if (work.IsCurrent?.Invoke() == false) throw new LastfmException(8);
        var app = await credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!app.IsConfigured) throw new LastfmException(10);
        if (work.ApplicationIdentity is { } expected && expected != ApplicationCredentialService.Identity(app)) throw new LastfmException(9);
        var values = new Dictionary<string, string>(work.Parameters, StringComparer.Ordinal)
        { ["method"] = work.Method, ["api_key"] = app.ApiKey };
        var signed = work.SessionKey is not null || work.Method.StartsWith("auth.", StringComparison.Ordinal);
        var cacheable = !signed && !work.Method.StartsWith("user.", StringComparison.Ordinal)
            && !work.Parameters.ContainsKey("user") && !work.Parameters.ContainsKey("username");
        if (work.SessionKey is not null) values["sk"] = work.SessionKey;
        if (signed) values["api_sig"] = LastfmSignature.Compute(values, app.Secret);
        values["format"] = "json";
        var cacheKey = "cache-" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray())));
        if (cacheable)
        {
            var cached = await store.ReadAsync<CachedResponse>(Guid.Empty, cacheKey, cancellationToken).ConfigureAwait(false);
            if (cached?.ExpiresAt > clock.GetUtcNow())
            {
                var document = JsonDocument.Parse(cached.Json);
                _responsePolicies.Add(document, new(false, cached.ExpiresAt));
                return document;
            }
            if (cached is not null) await store.DeleteAsync(Guid.Empty, cacheKey, cancellationToken).ConfigureAwait(false);
        }
        if (_suspendedKey == app.ApiKey) throw new LastfmException(26);
        var delay = _throttledUntil - clock.GetUtcNow();
        if (delay > TimeSpan.Zero) throw new LastfmException(29, delay);
        using var form = new FormUrlEncodedContent(values);
        var endpoint = "https://ws.audioscrobbler.com/2.0/";
        using var request = new HttpRequestMessage(signed ? HttpMethod.Post : HttpMethod.Get,
            signed ? endpoint : endpoint + "?" + await form.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (signed) request.Content = form;
        request.Headers.UserAgent.ParseAdd("JellySin.Lastfm/1.0 (+https://github.com/jellysin/plugin-lastfm)");
        if (work.IsCurrent?.Invoke() == false) throw new LastfmException(8);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return await ProcessResponseAsync(response, app.ApiKey, cacheable ? cacheKey : null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonDocument> ProcessResponseAsync(HttpResponseMessage response, string apiKey, string? cacheKey, CancellationToken cancellationToken)
    {
        var retryAfter = ReadRetryAfter(response);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            _throttledUntil = clock.GetUtcNow() + retryAfter;
            throw new LastfmException(29, retryAfter);
        }
        var bytes = await ReadBoundedAsync(response.Content, cancellationToken).ConfigureAwait(false);
        JsonDocument json;
        try { json = JsonDocument.Parse(bytes); }
        catch (JsonException) { throw new LastfmException(response.IsSuccessStatusCode || (int)response.StatusCode >= 500 ? 16 : 6); }
        try
        {
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new LastfmException(16);
            if (json.RootElement.TryGetProperty("error", out var error))
            {
                var code = error.ValueKind == JsonValueKind.Number && error.TryGetInt32(out var numeric) ? numeric
                    : error.ValueKind == JsonValueKind.String && int.TryParse(error.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 8;
                if (code == 29) _throttledUntil = clock.GetUtcNow() + retryAfter;
                if (code is 10 or 26) _suspendedKey = apiKey;
                throw new LastfmException(code, code == 29 ? retryAfter : null);
            }
            if (!response.IsSuccessStatusCode) throw new LastfmException((int)response.StatusCode >= 500 ? 16 : 6);
            _responsePolicies.Add(json, new ResponsePolicy(response.Headers.CacheControl?.NoStore == true, FreshUntil(response)));
            if (cacheKey is not null) await CacheAsync(cacheKey, bytes, response, cancellationToken).ConfigureAwait(false);
            return json;
        }
        catch { json.Dispose(); throw; }
    }

    private async Task CacheAsync(string key, byte[] bytes, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var control = response.Headers.CacheControl;
        if (control?.NoStore == true || control?.NoCache == true || control?.Private == true) return;
        var expires = FreshUntil(response);
        if (expires is null || expires <= clock.GetUtcNow()) return;
        try { await store.WriteAsync(Guid.Empty, key, new CachedResponse(expires.Value, Encoding.UTF8.GetString(bytes)), cancellationToken).ConfigureAwait(false); }
        catch (StorageBudgetException) { /* A cache miss must not prevent delivery or consume the durable reserve. */ }
    }

    private DateTimeOffset? FreshUntil(HttpResponseMessage response)
    {
        var control = response.Headers.CacheControl;
        if (control?.NoStore == true || control?.NoCache == true) return null;
        var age = response.Headers.Age ?? TimeSpan.Zero;
        return control?.MaxAge is { } maxAge ? clock.GetUtcNow() + maxAge - age : response.Content.Headers.Expires;
    }

    private TimeSpan ReadRetryAfter(HttpResponseMessage response)
    {
        var delay = response.Headers.RetryAfter?.Delta
            ?? (response.Headers.RetryAfter?.Date - clock.GetUtcNow()) ?? TimeSpan.FromMinutes(1);
        return TimeSpan.FromSeconds(Math.Clamp(delay.TotalSeconds, 1, 86_400));
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > MaxResponseBytes) throw new LastfmException(8);
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + read > MaxResponseBytes) throw new LastfmException(8);
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static Channel<Work> NewQueue() => Channel.CreateBounded<Work>(new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    public override void Dispose() { base.Dispose(); CancelPending(CancellationToken.None); _available.Dispose(); http.Dispose(); }

    public sealed record CachedResponse(DateTimeOffset ExpiresAt, string Json);

    private sealed record ResponsePolicy(bool NoStore, DateTimeOffset? ExpiresAt);

    private sealed record Work(string Method, Dictionary<string, string> Parameters, string? SessionKey, string? ApplicationIdentity, DateTimeOffset CreatedAt, CancellationToken CancellationToken, Func<bool>? IsCurrent)
    {
        public TaskCompletionSource<JsonDocument> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
