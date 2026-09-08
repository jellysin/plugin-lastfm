using System.Net;
using Jellyfin.Plugin.Lastfm.Api;
using Jellyfin.Plugin.Lastfm.Models;
using MediaBrowser.Controller.Entities.Audio;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Jellyfin.Plugin.Lastfm.Tests;

public sealed class LastfmApiClientTests
{
    private const string AcceptedScrobble = """{"scrobbles":{"@attr":{"accepted":"1","ignored":"0"}}}""";
    [Fact]
    public async Task AuthenticationUsesHttpsPostAndPreservesEncodedCredentials()
    {
        using var factory = TestHttpClientFactory.Responding("""{"session":{"name":"listener","key":"test-session","subscriber":"0"}}""");
        using var client = new LastfmApiClient(factory, NullLogger.Instance);
        var response = await client.RequestSession("listener+ß", "p&a=s+% word", TestContext.Current.CancellationToken);

        response.ShouldNotBeNull();
        response.Session.ShouldNotBeNull();
        response.Session.Key.ShouldBe("test-session");
        var request = factory.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Post);
        request.Uri.Scheme.ShouldBe("https");
        request.Uri.Host.ShouldBe("ws.audioscrobbler.com");
        request.Uri.Query.ShouldNotContain("password");
        request.ContentType.ShouldBe("application/x-www-form-urlencoded");
        request.Form["username"].ShouldBe("listener+ß");
        request.Form["password"].ShouldBe("p&a=s+% word");
        request.Form["method"].ShouldBe("auth.getMobileSession");
        request.Form["api_sig"].ShouldMatch("^[a-f0-9]{32}$");
    }

    [Fact]
    public async Task ApiErrorWithStringCodePreservesLastfmErrorContract()
    {
        using var factory = TestHttpClientFactory.Responding("""{"error":"4","message":"Authentication Failed"}""");
        using var client = new LastfmApiClient(factory, NullLogger.Instance);
        var response = await client.RequestSession("listener", "incorrect", TestContext.Current.CancellationToken);
        response.ShouldNotBeNull();
        response.IsError().ShouldBeTrue();
        response.ErrorCode.ShouldBe(4);
        response.Message.ShouldBe("Authentication Failed");
    }

    [Fact]
    public async Task ErrorLogsDoNotExposeRequestCredentialsOrRemoteResponseBodies()
    {
        using var factory = TestHttpClientFactory.Responding("""{"error":4,"message":"fake-password fake-session private-remote-body"}""");
        var logger = new Mock<ILogger>();
        using var client = new LastfmApiClient(factory, logger.Object);
        await client.RequestSession("listener", "fake-password", TestContext.Current.CancellationToken);
        var logs = logger.Invocations.Where(call => call.Method.Name == nameof(ILogger.Log))
            .Select(call => call.Arguments[2].ToString()).ToArray();
        logs.ShouldNotBeEmpty();
        foreach (var log in logs)
        {
            log.ShouldNotBeNull();
            log.ShouldNotContain("fake-password");
            log.ShouldNotContain("fake-session");
            log.ShouldNotContain("private-remote-body");
        }
    }

    [Theory]
    [InlineData("not json", HttpStatusCode.OK)]
    [InlineData("null", HttpStatusCode.OK)]
    [InlineData("{}", HttpStatusCode.BadGateway)]
    public async Task InvalidOrFailedResponseDoesNotBecomeSuccessfulAuthentication(string body, HttpStatusCode status)
    {
        using var factory = TestHttpClientFactory.Responding(body, status);
        using var client = new LastfmApiClient(factory, NullLogger.Instance);
        (await client.RequestSession("listener", "password", TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task AuthenticationPropagatesCancellationToInFlightHttpRequest()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var factory = new TestHttpClientFactory(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return TestHttpClientFactory.JsonResponse("{}");
        });
        using var client = new LastfmApiClient(factory, NullLogger.Instance);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var request = client.RequestSession("listener", "password", cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
    }

    [Fact]
    public async Task LovedTracksUsesBoundedPageQueryAndParsesStringPagination()
    {
        using var factory = TestHttpClientFactory.Responding("""{"lovedtracks":{"track":[],"@attr":{"page":"2","totalPages":"2","total":"1001"}}}""");
        using var client = new LastfmApiClient(factory, NullLogger.Instance);
        var response = await client.GetLovedTracks(User("listener & plus+"), TestContext.Current.CancellationToken, 2);
        response.ShouldNotBeNull();
        response.LovedTracks.ShouldNotBeNull();
        response.LovedTracks.Metadata.ShouldNotBeNull();
        response.LovedTracks.Metadata.IsLastPage().ShouldBeTrue();
        var request = factory.Requests.ShouldHaveSingleItem();
        request.Method.ShouldBe(HttpMethod.Get);
        request.Uri.Scheme.ShouldBe("https");
        var query = TestHttpClientFactory.ParseForm(request.Uri.Query);
        query["user"].ShouldBe("listener & plus+");
        query["limit"].ShouldBe("1000");
        query["page"].ShouldBe("2");
    }

    [Fact]
    public async Task NowPlayingAcceptsMissingAlbumArtistAndSendsDuration()
    {
        using var factory = TestHttpClientFactory.Responding("{}");
        using var client = new LastfmApiClient(factory, NullLogger.Instance);
        await client.NowPlaying(Track(), User(), TestContext.Current.CancellationToken);
        var form = factory.Requests.ShouldHaveSingleItem().Form;
        form["method"].ShouldBe("track.updateNowPlaying");
        form["duration"].ShouldBe("180");
        form["artist"].ShouldBe("Test Artist");
        form.ShouldNotContainKey("albumArtist");
    }

    [Fact]
    public async Task DuplicateScrobblesAreSuppressedForOneUserButNotAnother()
    {
        using var factory = TestHttpClientFactory.Responding(AcceptedScrobble);
        using var client = new LastfmApiClient(factory, NullLogger.Instance);
        var track = Track();
        await client.Scrobble(track, User("first"), TestContext.Current.CancellationToken);
        await client.Scrobble(track, User("first"), TestContext.Current.CancellationToken);
        await client.Scrobble(track, User("second"), TestContext.Current.CancellationToken);
        factory.Requests.Count.ShouldBe(2);
        factory.Requests.ShouldAllBe(request => request.Form["method"] == "track.scrobble");
    }

    [Fact]
    public async Task PendingScrobbleCapacityIsReleasedAfterCancellationAndAllowsRetry()
    {
        var block = true;
        var entered = 0;
        var full = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var factory = new TestHttpClientFactory(async (_, token) =>
        {
            if (Interlocked.Increment(ref entered) == 3)
            {
                full.TrySetResult();
            }
            if (block)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return TestHttpClientFactory.JsonResponse(AcceptedScrobble);
        });
        using var client = new LastfmApiClient(factory, NullLogger.Instance, maxPendingScrobbles: 3, maxCompletedScrobbles: 2);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var tracks = new[] { Track(), Track(), Track() };
        var pending = tracks.Select(track => client.Scrobble(track, User(), cancellation.Token)).ToArray();
        await full.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await client.Scrobble(Track(), User(), TestContext.Current.CancellationToken);
        await client.Scrobble(tracks[0], User(), TestContext.Current.CancellationToken);
        factory.Requests.Count.ShouldBe(3);
        await cancellation.CancelAsync();
        foreach (var request in pending)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        }

        block = false;
        await client.Scrobble(tracks[0], User(), TestContext.Current.CancellationToken);
        factory.Requests.Count.ShouldBe(4);
    }

    [Fact]
    public async Task CompletedScrobbleCacheEvictsOldestEntryAndExpiresRemainingDuplicates()
    {
        using var factory = TestHttpClientFactory.Responding(AcceptedScrobble);
        var clock = new TestTimeProvider();
        using var client = new LastfmApiClient(factory, NullLogger.Instance, maxPendingScrobbles: 3, maxCompletedScrobbles: 2, timeProvider: clock);
        var first = Track();
        var second = Track();
        var third = Track();
        await client.Scrobble(first, User(), TestContext.Current.CancellationToken);
        clock.Advance(1);
        await client.Scrobble(second, User(), TestContext.Current.CancellationToken);
        clock.Advance(1);
        await client.Scrobble(third, User(), TestContext.Current.CancellationToken);
        await client.Scrobble(second, User(), TestContext.Current.CancellationToken);
        factory.Requests.Count.ShouldBe(3);

        await client.Scrobble(first, User(), TestContext.Current.CancellationToken);
        factory.Requests.Count.ShouldBe(4);
        clock.Advance(16);
        await client.Scrobble(third, User(), TestContext.Current.CancellationToken);
        factory.Requests.Count.ShouldBe(5);
    }

    [Fact]
    public async Task FailedScrobbleCanBeRetriedImmediately()
    {
        var calls = 0;
        using var factory = new TestHttpClientFactory((_, _) => Task.FromResult(
            ++calls == 1 ? TestHttpClientFactory.JsonResponse("{}", HttpStatusCode.ServiceUnavailable) : TestHttpClientFactory.JsonResponse(AcceptedScrobble)));
        using var client = new LastfmApiClient(factory, NullLogger.Instance);
        var track = Track();
        await client.Scrobble(track, User(), TestContext.Current.CancellationToken);
        await client.Scrobble(track, User(), TestContext.Current.CancellationToken);
        factory.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public async Task ScrobbleTimestampIsPlaybackStartRatherThanSubmissionTime()
    {
        using var factory = TestHttpClientFactory.Responding(AcceptedScrobble);
        using var client = new LastfmApiClient(factory, NullLogger.Instance);
        var startedAt = DateTimeOffset.FromUnixTimeSeconds(1704067200);
        await client.Scrobble(Track(), User(), TestContext.Current.CancellationToken, startedAt);
        factory.Requests.ShouldHaveSingleItem().Form["timestamp"].ShouldBe("1704067200");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"scrobbles\":{\"@attr\":{\"accepted\":\"0\",\"ignored\":\"1\"}}}")]
    public async Task EmptyOrIgnoredScrobbleResponseDoesNotSuppressRetry(string response)
    {
        var calls = 0;
        using var factory = new TestHttpClientFactory((_, _) => Task.FromResult(TestHttpClientFactory.JsonResponse(++calls == 1 ? response : AcceptedScrobble)));
        using var client = new LastfmApiClient(factory, NullLogger.Instance);
        var track = Track();
        await client.Scrobble(track, User(), TestContext.Current.CancellationToken);
        await client.Scrobble(track, User(), TestContext.Current.CancellationToken);
        await client.Scrobble(track, User(), TestContext.Current.CancellationToken);
        factory.Requests.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(true, "track.love")]
    [InlineData(false, "track.unlove")]
    public async Task LoveOperationsPreserveTheRequestedState(bool loved, string method)
    {
        using var factory = TestHttpClientFactory.Responding("{}");
        using var client = new LastfmApiClient(factory, NullLogger.Instance);
        (await client.LoveTrack(Track(), User(), loved, TestContext.Current.CancellationToken)).ShouldBeTrue();
        factory.Requests.ShouldHaveSingleItem().Form["method"].ShouldBe(method);
    }

    private static Audio Track() => new() { Id = Guid.NewGuid(), Name = "Test Song", Artists = ["Test Artist"], AlbumArtists = [], RunTimeTicks = 180 * TimeSpan.TicksPerSecond };

    private static LastfmUser User(string username = "listener") => new() { Username = username, SessionKey = "fake-session", Options = new LastFmUserOptions { Scrobble = true, SyncFavourites = true } };
}
