using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JellySin.Plugin.Lastfm.Storage;

namespace JellySin.Plugin.Lastfm.Features;

public sealed class MusicViewCache(IStateStore store, TimeProvider clock)
{
    public async Task<T?> ReadAsync<T>(Guid userId, string identity, CancellationToken ct) where T : class
    {
        try
        {
            var saved = await store.ReadAsync<CachedView<T>>(userId, Key(identity), ct).ConfigureAwait(false);
            return saved?.ExpiresAt > clock.GetUtcNow() ? saved.Value : null;
        }
        catch (JsonException)
        {
            await store.DeleteAsync(userId, Key(identity), ct).ConfigureAwait(false);
            return null;
        }
    }

    public async Task WriteAsync<T>(Guid userId, string identity, T value, TimeSpan freshness, CancellationToken ct) where T : class
    {
        if (freshness <= TimeSpan.Zero) return;
        freshness = freshness > TimeSpan.FromMinutes(5) ? TimeSpan.FromMinutes(5) : freshness;
        try { await store.WriteAsync(userId, Key(identity), new CachedView<T>(value, clock.GetUtcNow() + freshness), ct).ConfigureAwait(false); }
        catch (StorageBudgetException) { } // A full optional cache never prevents showing an already retrieved view.
    }

    private static string Key(string identity) => "cache-view-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    private sealed record CachedView<T>(T Value, DateTimeOffset ExpiresAt);
}
