using System.Text.Json;
using JellySin.Plugin.Lastfm.Playback;
using JellySin.Plugin.Lastfm.Transport;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class OutboxTests
{
    [Fact]
    public async Task MalformedPersistedMetadataIsRejectedWithoutBlockingFollowingValidListen()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var invalid = Listen(fixture);
        invalid = invalid with { Track = invalid.Track with { Title = new string('x', 5000) } };
        await fixture.Store.WriteAsync(fixture.UserId, "outbox", new OutboxState([new(invalid), new(Listen(fixture))], []), TestContext.Current.CancellationToken);
        var outbox = NewOutbox(fixture);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        var status = await outbox.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.Equal(0, status.Pending);
        Assert.Equal(1, status.Rejected);
        Assert.Equal(-1, status.LastRejectedCode);
        Assert.Single(fixture.Client.Calls, call => call.Method == "track.scrobble");
    }
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(99)]
    public async Task PartialDailyFutureAndUnknownRejectionsRemainVisibleAndRetryable(int ignored)
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var outbox = NewOutbox(fixture);
        await outbox.EnqueueAsync(Listen(fixture), TestContext.Current.CancellationToken);
        await outbox.EnqueueAsync(Listen(fixture), TestContext.Current.CancellationToken);
        fixture.Client.Handler = (_, _, _) => JsonSerializer.SerializeToDocument(new
        { scrobbles = new { scrobble = new[] { new { ignoredMessage = new { code = 0 } }, new { ignoredMessage = new { code = ignored } } } } });
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        var status = await outbox.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.Equal(1, status.Pending);
        Assert.Equal(1, status.Blocked);
        Assert.Equal(ignored, status.LastIgnoredCode);
        Assert.Null(status.LastErrorCode);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.Single(fixture.Client.Calls, call => call.Method == "track.scrobble");
        await outbox.ResumeAsync(fixture.UserId, TestContext.Current.CancellationToken);
        fixture.Client.Handler = null;
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.Equal(0, (await outbox.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Pending);
    }

    [Fact]
    public async Task OversizedOptionalMetadataCannotPoisonTheDeliveryBatch()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var outbox = NewOutbox(fixture);
        var listen = Listen(fixture);
        await outbox.EnqueueAsync(listen with { Track = listen.Track with { Album = new string('x', 5000), MusicBrainzId = new string('y', 5000) } }, TestContext.Current.CancellationToken);
        fixture.Client.Handler = (_, parameters, _) =>
        {
            Assert.DoesNotContain("album[0]", parameters.Keys);
            Assert.DoesNotContain("mbid[0]", parameters.Keys);
            return JsonDocument.Parse("""{"scrobbles":{"scrobble":{"ignoredMessage":{"code":0}}}}""");
        };
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.Equal(0, (await outbox.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Pending);
    }

    [Fact]
    public async Task OldAccountAndNowPlayingGenerationsCannotReachReconnectedAccount()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var listen = Listen(fixture);
        var outbox = NewOutbox(fixture);
        await fixture.Accounts.DisconnectAsync(fixture.UserId, TestContext.Current.CancellationToken);
        await fixture.ConnectAsync();
        await outbox.EnqueueAsync(listen, TestContext.Current.CancellationToken);
        await outbox.NowPlayingAsync(listen, TestContext.Current.CancellationToken);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(fixture.Client.Calls, call => call.Method.StartsWith("track.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EligibleListenPersistsDuringPauseAndDeliversOnlyAfterResume()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var listen = Listen(fixture);
        var outbox = NewOutbox(fixture);
        await fixture.Accounts.SetScrobblingAsync(fixture.UserId, false, TestContext.Current.CancellationToken);
        await outbox.EnqueueAsync(listen, TestContext.Current.CancellationToken);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.Equal(1, (await outbox.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Pending);
        await fixture.Accounts.SetScrobblingAsync(fixture.UserId, true, TestContext.Current.CancellationToken);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.Equal(0, (await outbox.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Pending);
    }

    [Fact]
    public async Task CompletedOccurrenceIsNotResubmittedAcrossRestart()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var outbox = NewOutbox(fixture);
        var listen = Listen(fixture);
        await outbox.EnqueueAsync(listen, TestContext.Current.CancellationToken);
        await outbox.EnqueueAsync(listen, TestContext.Current.CancellationToken);
        await NewOutbox(fixture).FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        await outbox.EnqueueAsync(listen, TestContext.Current.CancellationToken);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.Single(fixture.Client.Calls, call => call.Method == "track.scrobble");
        Assert.Equal(0, (await outbox.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Pending);
    }

    [Fact]
    public async Task PartialAcceptanceRecordsEachResultAndOriginalTimestamp()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var outbox = NewOutbox(fixture);
        var first = Listen(fixture);
        var second = Listen(fixture) with { StartedAt = fixture.Clock.GetUtcNow().AddSeconds(1) };
        await outbox.EnqueueAsync(first, TestContext.Current.CancellationToken);
        await outbox.EnqueueAsync(second, TestContext.Current.CancellationToken);
        fixture.Client.Handler = (method, values, _) =>
        {
            Assert.Equal("track.scrobble", method);
            Assert.Equal(first.StartedAt.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), values["timestamp[0]"]);
            return JsonDocument.Parse("{\"scrobbles\":{\"scrobble\":[{\"ignoredMessage\":{\"code\":\"0\"}},{\"ignoredMessage\":{\"code\":\"2\"}}]}}");
        };
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        var state = await fixture.Store.ReadAsync<OutboxState>(fixture.UserId, "outbox", TestContext.Current.CancellationToken);
        Assert.Empty(state!.Pending);
        Assert.Equal([0, 2], state.Receipts.Select(receipt => receipt.IgnoredCode));
    }

    [Fact]
    public async Task NewEntriesAddedDuringSubmissionArePreserved()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var outbox = NewOutbox(fixture);
        var first = Listen(fixture);
        await outbox.EnqueueAsync(first, TestContext.Current.CancellationToken);
        fixture.Client.AsyncHandler = async (_, _, cancellationToken) =>
        {
            await outbox.EnqueueAsync(Listen(fixture), cancellationToken);
            return JsonDocument.Parse("{\"scrobbles\":{\"scrobble\":{\"ignoredMessage\":{\"code\":0}}}}");
        };
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.Equal(1, (await outbox.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Pending);
    }

    [Theory]
    [InlineData(29)]
    [InlineData(10)]
    [InlineData(13)]
    [InlineData(26)]
    public async Task PermanentAndRateErrorsParkWithoutLosingEntries(int error)
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var outbox = NewOutbox(fixture);
        await outbox.EnqueueAsync(Listen(fixture), TestContext.Current.CancellationToken);
        fixture.Client.Handler = (_, _, _) => throw new LastfmException(error);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        var status = await outbox.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.Equal(1, status.Pending);
        Assert.Equal(1, status.Blocked);
        Assert.Equal(error, status.LastErrorCode);
        Assert.Single(fixture.Client.Calls, call => call.Method == "track.scrobble");
    }

    [Theory]
    [InlineData(11)]
    [InlineData(16)]
    public async Task TemporaryErrorsWaitBeforeRetry(int error)
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var outbox = NewOutbox(fixture);
        await outbox.EnqueueAsync(Listen(fixture), TestContext.Current.CancellationToken);
        fixture.Client.Handler = (_, _, _) => throw new LastfmException(error);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.Single(fixture.Client.Calls, call => call.Method == "track.scrobble");
        fixture.Clock.Advance(31);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.Client.Calls.Count(call => call.Method == "track.scrobble"));
    }

    [Fact]
    public async Task InvalidSessionPausesAccountAndRetainsListen()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var outbox = NewOutbox(fixture);
        await outbox.EnqueueAsync(Listen(fixture), TestContext.Current.CancellationToken);
        fixture.Client.Handler = (_, _, _) => throw new LastfmException(9);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.True((await fixture.Accounts.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).NeedsReconnect);
        Assert.Equal(1, (await outbox.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Pending);
    }

    [Fact]
    public async Task MalformedBatchResultDoesNotSilentlyAcknowledgeAnything()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var outbox = NewOutbox(fixture);
        await outbox.EnqueueAsync(Listen(fixture), TestContext.Current.CancellationToken);
        fixture.Client.Handler = (_, _, _) => JsonDocument.Parse("{\"scrobbles\":{\"scrobble\":[]}}");
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.Equal(1, (await outbox.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Pending);
    }

    [Fact]
    public async Task StaleNowPlayingIsDiscarded()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var listen = Listen(fixture);
        fixture.Clock.Advance(21);
        await NewOutbox(fixture).NowPlayingAsync(listen, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(fixture.Client.Calls, call => call.Method == "track.updateNowPlaying");
    }

    [Fact]
    public async Task DisabledScrobblingDoesNotEnqueue()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        await fixture.Accounts.SetScrobblingAsync(fixture.UserId, false, TestContext.Current.CancellationToken);
        var outbox = NewOutbox(fixture);
        await outbox.EnqueueAsync(Listen(fixture), TestContext.Current.CancellationToken);
        Assert.Equal(0, (await outbox.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Pending);
    }

    [Fact]
    public async Task ExplicitResumeReleasesRateLimitedPendingEntries()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var outbox = NewOutbox(fixture);
        await outbox.EnqueueAsync(Listen(fixture), TestContext.Current.CancellationToken);
        fixture.Client.Handler = (_, _, _) => throw new LastfmException(29);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        await outbox.ResumeAsync(fixture.UserId, TestContext.Current.CancellationToken);
        fixture.Client.Handler = null;
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.Equal(0, (await outbox.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Pending);
    }

    [Fact]
    public async Task ReconnectAutomaticallyReleasesOnlyInvalidSessionFailures()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var outbox = NewOutbox(fixture);
        await outbox.EnqueueAsync(Listen(fixture), TestContext.Current.CancellationToken);
        fixture.Client.Handler = (_, _, _) => throw new LastfmException(9);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        fixture.Client.Handler = null;
        await fixture.ConnectAsync();
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.Equal(0, (await outbox.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Pending);
    }

    [Fact]
    public async Task FreshNowPlayingUsesLinkedSessionWithoutPersistingListen()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var outbox = NewOutbox(fixture);
        await outbox.NowPlayingAsync(Listen(fixture), TestContext.Current.CancellationToken);
        Assert.Single(fixture.Client.Calls, call => call.Method == "track.updateNowPlaying" && call.Session == "private-session-value");
        Assert.Equal(0, (await outbox.GetStatusAsync(fixture.UserId, TestContext.Current.CancellationToken)).Pending);
    }

    private static ScrobbleOutbox NewOutbox(CoreFixture fixture) => new(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock);
    private static EligibleListen Listen(CoreFixture fixture) => new(Guid.NewGuid(), fixture.UserId, new MusicTrack(Guid.NewGuid(), "Artist", "Song", "Album", null, 180), fixture.Clock.GetUtcNow(),
        fixture.Accounts.GetCaptureBinding(fixture.UserId)?.AccountGeneration ?? Guid.Empty, fixture.Accounts.GetCaptureBinding(fixture.UserId)?.CaptureGeneration ?? Guid.Empty);
}
