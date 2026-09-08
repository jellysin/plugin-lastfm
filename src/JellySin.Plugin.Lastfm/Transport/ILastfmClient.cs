using System.Text.Json;

namespace JellySin.Plugin.Lastfm.Transport;

public enum RequestPriority { Listening, Interactive, Background }

public interface ILastfmClient
{
    Task<JsonDocument> CallAsync(string method, IReadOnlyDictionary<string, string> parameters, string? sessionKey, RequestPriority priority, CancellationToken cancellationToken);

    bool CanPersist(JsonDocument response) => true;
}

public sealed class LastfmException(int code, TimeSpan? retryAfter = null) : Exception($"Last.fm request failed with code {code}.")
{
    public int Code { get; } = code;

    public bool Retryable => Code is 11 or 16;

    public TimeSpan? RetryAfter { get; } = retryAfter;
}
