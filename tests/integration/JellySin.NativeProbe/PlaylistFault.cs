using System.Reflection;
using System.Runtime.ExceptionServices;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Playlists;

namespace JellySin.NativeProbe;

public sealed class PlaylistFaultState
{
    private readonly object gate = new();
    private (Guid User, Guid Playlist)? armed;
    private bool preparing;
    public bool Configured { get; set; }
    public bool Cleared { get; private set; }

    public IDisposable? TryBeginPreparation()
    {
        lock (gate)
        {
            if (!Configured || preparing || armed is not null) return null;
            preparing = true;
            return new Preparation(this);
        }
    }

    public void Arm(Guid userId, Guid playlistId)
    {
        lock (gate)
        {
            if (!Configured || !preparing || armed is not null || userId == Guid.Empty || playlistId == Guid.Empty)
                throw new InvalidOperationException("The test-only playlist fault cannot be armed.");
            armed = (userId, playlistId);
            Cleared = false;
        }
    }

    public bool Consume(PlaylistUpdateRequest request)
    {
        lock (gate)
        {
            if (armed != (request.UserId, request.Id)) return false;
            armed = null;
            return true;
        }
    }

    public void MarkCleared() { lock (gate) Cleared = true; }

    private sealed class Preparation(PlaylistFaultState owner) : IDisposable
    {
        private PlaylistFaultState? current = owner;
        public void Dispose()
        {
            var state = Interlocked.Exchange(ref current, null);
            if (state is not null) { lock (state.gate) state.preparing = false; }
        }
    }
}

/// <summary>One armed test operation clears through the real native manager, then fails before adding.</summary>
public class PlaylistFaultProxy : DispatchProxy, IDisposable
{
    private IPlaylistManager inner = null!;
    private PlaylistFaultState state = null!;

    public void Initialize(IPlaylistManager manager, PlaylistFaultState fault)
    {
        inner = manager;
        state = fault;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name == nameof(IPlaylistManager.UpdatePlaylist)
            && args is [PlaylistUpdateRequest request] && state.Consume(request)) return InterruptAsync(request);
        try { return targetMethod!.Invoke(inner, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            return null;
        }
    }

    private async Task InterruptAsync(PlaylistUpdateRequest request)
    {
        await inner.UpdatePlaylist(new PlaylistUpdateRequest
        { Id = request.Id, UserId = request.UserId, Name = request.Name, Ids = [], Public = false }).ConfigureAwait(false);
        state.MarkCleared();
        throw new IOException("Injected fixture failure after native playlist clear and before add.");
    }

    public void Dispose() { (inner as IDisposable)?.Dispose(); }
}
