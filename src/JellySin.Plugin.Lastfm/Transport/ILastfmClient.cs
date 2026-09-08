using System.Text.Json;

namespace JellySin.Plugin.Lastfm.Transport;

public enum RequestPriority { Listening, Interactive, Background }

public interface ILastfmClient
{
    Task<JsonDocument> CallAsync(string method, IReadOnlyDictionary<string, string> parameters, string? sessionKey, RequestPriority priority, CancellationToken cancellationToken);

    Task<JsonDocument> CallForApplicationAsync(string method, IReadOnlyDictionary<string, string> parameters, string? sessionKey,
        string applicationIdentity, RequestPriority priority, CancellationToken cancellationToken)
        => CallAsync(method, parameters, sessionKey, priority, cancellationToken);

    Task<JsonDocument> UpdateNowPlayingAsync(IReadOnlyDictionary<string, string> parameters, string sessionKey,
        string applicationIdentity, Func<bool> isCurrent, CancellationToken cancellationToken)
        => isCurrent() ? CallForApplicationAsync("track.updateNowPlaying", parameters, sessionKey, applicationIdentity, RequestPriority.Listening, cancellationToken)
            : Task.FromException<JsonDocument>(new LastfmException(8));

    bool CanPersist(JsonDocument response) => true;

    TimeSpan GetCacheLifetime(JsonDocument response) => TimeSpan.Zero;
}

public sealed class LastfmException(int code, TimeSpan? retryAfter = null) : Exception($"Last.fm request failed with code {code}.")
{
    public int Code { get; } = code;

    public bool Retryable => Code is 11 or 16;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}
