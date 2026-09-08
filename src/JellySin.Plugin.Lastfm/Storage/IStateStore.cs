namespace JellySin.Plugin.Lastfm.Storage;

public interface IStateStore
{
    Task<T?> ReadAsync<T>(Guid userId, string key, CancellationToken cancellationToken);

    Task WriteAsync<T>(Guid userId, string key, T value, CancellationToken cancellationToken);

    Task<T> UpdateAsync<T>(Guid userId, string key, Func<T?, T> update, CancellationToken cancellationToken);

    Task DeleteAsync(Guid userId, string key, CancellationToken cancellationToken);

    Task DeleteUserAsync(Guid userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> GetUsersAsync(CancellationToken cancellationToken);

    Task ReserveNativeAsync(string identity, long bytes, CancellationToken cancellationToken);
}

public sealed class StorageBudgetException() : IOException("The Last.fm storage budget has been reached. Remove cached data before continuing.");

public sealed class AccountDisconnectedException() : InvalidOperationException("Last.fm has been disconnected for this user.");
