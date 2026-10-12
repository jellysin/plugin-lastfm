namespace JellySin.Plugin.Lastfm.Tests.Features;

public sealed class FavouritesRequestBoundTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
}
