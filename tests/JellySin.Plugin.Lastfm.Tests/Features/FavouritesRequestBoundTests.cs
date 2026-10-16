using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Tests.Core;
using JellySin.Plugin.Lastfm.Transport;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;

namespace JellySin.Plugin.Lastfm.Tests.Features;

public sealed class FavouritesRequestBoundTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static int LovedReads(FeatureFixture f) => f.Core.Client.Calls.Count(c => c.Method == "user.getLovedTracks");

    [Fact]
    public async Task AnAbandonedSaveRequestDoesNotStopTheReconciliation()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var local = f.AddLocal("Loved on Last.fm");
        f.Loved.Add(local.Track);
        var matching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        f.Library.OnMatch = ct => { matching.TrySetResult(); release.Wait(ct); };
        using var request = new CancellationTokenSource();
        var save = Task.Run(() => f.SaveFromRequestAsync(true, request.Token), Ct);
        await matching.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        // The browser gives up, as it does when its own time limit ends or the page is left.
        await request.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
        release.Set();
        await f.ReconciliationEndedAsync(Ct);
        Assert.NotNull((await f.Favourites.GetReviewAsync(f.Core.UserId, Ct)).LastSync);
        Assert.True(f.Library.Items[local.Id].Favourite);
    }

    [Fact]
    public async Task RequestWithShortWaitAnswersWithStoredStateWhileRunIsHeld()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var local = f.AddLocal("Loved on Last.fm");
        f.Loved.Add(local.Track);
        using var runs = new FavouriteRuns(f.Favourites, f.Core.Clock, NullLogger<FavouriteRuns>.Instance)
        {
            RequestWait = TimeSpan.FromMilliseconds(50),
        };
        var matching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        f.Library.OnMatch = ct => { matching.TrySetResult(); release.Wait(ct); };

        var review = await runs.SetEnabledAsync(f.Core.UserId, true, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await matching.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.True(runs.IsRunning(f.Core.UserId));
        Assert.Null(review.LastSync);

        release.Set();
        await runs.RunToEndAsync(f.Core.UserId, Ct);

        var finished = await f.Favourites.GetReviewAsync(f.Core.UserId, Ct);
        Assert.NotNull(finished.LastSync);
        Assert.False(runs.IsRunning(f.Core.UserId));
    }

    [Fact]
    public async Task TwoRequestsWhileRunIsHeldCauseOneLovedTracksCall()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var local = f.AddLocal("Loved on Last.fm");
        f.Loved.Add(local.Track);
        var matching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        f.Library.OnMatch = ct => { matching.TrySetResult(); release.Wait(ct); };

        var first = f.Runs.SetEnabledAsync(f.Core.UserId, true, Ct);
        await matching.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        // One run reads the collection and reads its first page again to see that it did not change.
        Assert.Equal(2, LovedReads(f));
        // The second request joins the held run before this call returns its task.
        var second = f.Runs.SyncAsync(f.Core.UserId, Ct);

        release.Set();
        var reviews = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.All(reviews, review => Assert.NotNull(review.LastSync));
        Assert.Equal(2, LovedReads(f));
    }

    [Fact]
    public async Task WithoutConnectionSetEnabledStoresPreferenceAndThrowsNotConnected()
    {
        using var f = new FeatureFixture();
        f.Library.UserId = f.Core.UserId;
        f.Core.Client.Handler = f.Respond;
        var logger = new FakeLogger<FavouriteRuns>();
        using var runs = new FavouriteRuns(f.Favourites, f.Core.Clock, logger);

        await Assert.ThrowsAsync<NotConnectedException>(() => runs.SetEnabledAsync(f.Core.UserId, true, Ct));

        var review = await f.Favourites.GetReviewAsync(f.Core.UserId, Ct);
        Assert.True(review.Enabled);
        // A missing connection is the page's own answer, not a failed run.
        Assert.Null(review.Status);
        Assert.Empty(logger.Lines);
    }

    [Fact]
    public async Task SetEnabledFalseDuringHeldRunCancelsRunAndStoresPreference()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var local = f.AddLocal("Held track");
        f.Loved.Add(local.Track);
        var matching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        f.Library.OnMatch = ct => { matching.TrySetResult(); release.Wait(ct); };

        var start = f.Runs.SetEnabledAsync(f.Core.UserId, true, Ct);
        await matching.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(f.Runs.IsRunning(f.Core.UserId));

        // The run is still held: only its cancellation lets this call return.
        var review = await f.Runs.SetEnabledAsync(f.Core.UserId, false, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.False(review.Enabled);
        Assert.False(f.Runs.IsRunning(f.Core.UserId));

        // The request that was waiting for the stopped run answers with the stored state, not with an error.
        var waiting = await start.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Null(waiting.LastSync);
        Assert.False(f.Library.Items[local.Id].Favourite);
        Assert.False((await f.Favourites.GetReviewAsync(f.Core.UserId, Ct)).Enabled);
    }

    [Fact]
    public async Task RunFailingAfterTheRequestGaveUpLeavesTheUnfinishedStatusAndOneWarningWithoutDetails()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var local = f.AddLocal("Loved on Last.fm");
        f.Loved.Add(local.Track);
        var logger = new FakeLogger<FavouriteRuns>();
        using var runs = new FavouriteRuns(f.Favourites, f.Core.Clock, logger)
        {
            RequestWait = TimeSpan.FromMilliseconds(50),
        };
        var matching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        f.Library.OnMatch = ct =>
        {
            matching.TrySetResult();
            release.Wait(ct);
            throw new IOException("planted-detail");
        };

        var review = await runs.SetEnabledAsync(f.Core.UserId, true, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await matching.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Null(review.Status);

        release.Set();
        await runs.RunToEndAsync(f.Core.UserId, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var stored = await f.Favourites.GetReviewAsync(f.Core.UserId, Ct);
        Assert.Equal("The last synchronization did not finish. Synchronize again.", stored.Status);
        Assert.Null(stored.LastSync);
        var line = Assert.Single(logger.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Equal($"Favourite synchronization for user {Logged.User(f.Core.UserId)} failed with IOException.", line.Text);
    }

    [Fact]
    public async Task RunFailingWhileTheRequestWaitsAnswersWithThatFailure()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        f.Core.Client.Handler = (_, _, _) => throw new LastfmException(16);

        var failure = await Assert.ThrowsAsync<LastfmException>(() => f.Runs.SetEnabledAsync(f.Core.UserId, true, Ct));

        Assert.Equal(16, failure.Code);
        Assert.False(f.Runs.IsRunning(f.Core.UserId));
    }

    [Fact]
    public async Task ReviewRemovalAsyncThrowsWhenUserLockIsHeld()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var favourites = new FavouritesService(f.Api, f.Library, f.Library, f.Core.Store, f.Locks, f.Core.Clock, f.Core.Accounts, f.FavouriteLog)
        {
            LockWait = TimeSpan.FromMilliseconds(50),
        };

        using var lease = await f.Locks.EnterAsync(f.Core.UserId, Ct);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            favourites.ReviewRemovalAsync(f.Core.UserId, Guid.NewGuid(), true, Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct));

        Assert.Equal("Favourites are being synchronized. Try again when it has finished.", ex.Message);
    }

    [Fact]
    public async Task DisposeDuringHeldRunEndsTheRun()
    {
        using var f = new FeatureFixture();
        await f.InitializeAsync();
        var local = f.AddLocal("Held track");
        f.Loved.Add(local.Track);
        var matching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        f.Library.OnMatch = ct => { matching.TrySetResult(); release.Wait(ct); };

        await f.Favourites.SetEnabledAsync(f.Core.UserId, true, Ct);
        var ended = f.Runs.RunToEndAsync(f.Core.UserId, Ct);
        await matching.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(f.Runs.IsRunning(f.Core.UserId));

        f.Runs.Dispose();

        await ended.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.False(f.Runs.IsRunning(f.Core.UserId));
        Assert.Null((await f.Favourites.GetReviewAsync(f.Core.UserId, Ct)).LastSync);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => f.Runs.RunToEndAsync(f.Core.UserId, Ct));
    }
}
