using System.Runtime.ExceptionServices;
using JellySin.Plugin.Lastfm.Api;
using JellySin.Plugin.Lastfm.Configuration;
using Microsoft.Extensions.Logging;

namespace JellySin.Plugin.Lastfm.Features;

/// <summary>
/// Runs the favourite reconciliation outside the web request that asked for it: one run per user, two at once.
/// A request waits a short time for the result and leaves the run alone when it gives up.
/// </summary>
public sealed class FavouriteRuns(FavouritesService favourites, TimeProvider clock, ILogger<FavouriteRuns> logger) : IDisposable
{
    private const int MaximumRuns = 256;
    private readonly object gate = new();
    private readonly Dictionary<Guid, Run> runs = [];
    private readonly SemaphoreSlim slots = new(2, 2);
    private readonly CancellationTokenSource stopping = new();
    private bool disposed;

    public TimeSpan RequestWait { get; init; } = TimeSpan.FromSeconds(10);

    public bool IsRunning(Guid userId)
    {
        lock (gate) return runs.ContainsKey(userId);
    }

    /// <summary>Starts the user's run or joins it, and answers with the stored state when it has not ended in time.</summary>
    public async Task<FavouriteReview> SyncAsync(Guid userId, CancellationToken request)
    {
        var run = StartOrJoin(userId);
        if (await EndedInTimeAsync(run, request).ConfigureAwait(false) && await run.Completion.ConfigureAwait(false) is { } failure)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return await favourites.GetReviewAsync(userId, request).ConfigureAwait(false);
    }

    public async Task<FavouriteReview> SetEnabledAsync(Guid userId, bool enabled, CancellationToken request)
    {
        var review = await favourites.GetReviewAsync(userId, request).ConfigureAwait(false);
        if (review.Enabled != enabled)
        {
            await StopAsync(userId, request).ConfigureAwait(false);
            review = await favourites.SetEnabledAsync(userId, enabled, request).ConfigureAwait(false);
        }
        return enabled ? await SyncAsync(userId, request).ConfigureAwait(false) : review;
    }

    /// <summary>Waits for the user's run; the token ends the waiting, not the run.</summary>
    public async Task RunToEndAsync(Guid userId, CancellationToken ct) =>
        await StartOrJoin(userId).Completion.WaitAsync(ct).ConfigureAwait(false);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        stopping.Cancel();
        stopping.Dispose();
        slots.Dispose();
    }

    private async Task<bool> EndedInTimeAsync(Run run, CancellationToken request)
    {
        try
        {
            await run.Completion.WaitAsync(RequestWait, clock, request).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException) { return false; }
    }

    private async Task StopAsync(Guid userId, CancellationToken request)
    {
        Run? run;
        lock (gate) runs.TryGetValue(userId, out run);
        if (run is null) return;
        run.Cancel();
        await run.Completion.WaitAsync(request).ConfigureAwait(false);
    }

    private Run StartOrJoin(Guid userId)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (runs.TryGetValue(userId, out var existing)) return existing;
            if (runs.Count >= MaximumRuns) throw new InvalidOperationException("Too many favourite synchronizations are waiting.");
            var run = new Run(stopping.Token);
            runs.Add(userId, run);
            // The reconciliation matches the library synchronously; it must not run on the caller's thread.
            _ = Task.Run(() => ExecuteAsync(userId, run), CancellationToken.None);
            return run;
        }
    }

    private async Task ExecuteAsync(Guid userId, Run run)
    {
        Exception? failure = null;
        try
        {
            await slots.WaitAsync(run.Token).ConfigureAwait(false);
            try { await favourites.SyncAsync(userId, run.Token).ConfigureAwait(false); }
            finally { slots.Release(); }
        }
        catch (Exception exception) { failure = exception; }
        // A run that was stopped (feature switched off, connection changed, server stopping) has nothing to report.
        if (failure is OperationCanceledException || run.Token.IsCancellationRequested) failure = null;
        if (failure is not null and not NotConnectedException) await NoteFailureAsync(userId, failure, run.Token).ConfigureAwait(false);
        lock (gate) runs.Remove(userId);
        run.End(failure);
    }

    private async Task NoteFailureAsync(Guid userId, Exception failure, CancellationToken ct)
    {
        logger.LogWarning("Favourite synchronization for user {User} failed with {ErrorType}.", LogName.Of(userId), failure.GetType().Name);
        try { await favourites.NoteUnfinishedAsync(userId, ct).ConfigureAwait(false); }
        catch (Exception exception)
        {
            logger.LogWarning("Storing the state of the favourite synchronization for user {User} failed with {ErrorType}.",
                LogName.Of(userId), exception.GetType().Name);
        }
    }

    private sealed class Run
    {
        private readonly object sync = new();
        private readonly CancellationTokenSource source;
        private readonly TaskCompletionSource<Exception?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool ended;

        public Run(CancellationToken stopping)
        {
            source = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            Token = source.Token;
        }

        public CancellationToken Token { get; }

        /// <summary>Completes with the failure a request has to answer with, or with none. It never faults.</summary>
        public Task<Exception?> Completion => completion.Task;

        public void Cancel()
        {
            lock (sync)
            {
                if (!ended) source.Cancel();
            }
        }

        public void End(Exception? failure)
        {
            lock (sync) ended = true;
            source.Dispose();
            completion.TrySetResult(failure);
        }
    }
}
