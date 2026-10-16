using System.Text.Json;
using System.Web;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Playback;
using JellySin.Plugin.Lastfm.Storage;
using JellySin.Plugin.Lastfm.Transport;
using Microsoft.AspNetCore.DataProtection;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class CoreFixture : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "jellysin-tests", Guid.NewGuid().ToString("N"));
    public FileStateStore Store { get; }
    public TestClock Clock { get; } = new();
    public StubClient Client { get; } = new();
    public IDataProtectionProvider Protection { get; } = new EphemeralDataProtectionProvider();
    public AccountService Accounts { get; }
    public ApplicationCredentialService Credentials { get; }
    public LibraryRuleService LibraryRules { get; }
    public Guid UserId { get; } = Guid.NewGuid();
    public static Uri Callback { get; } = new("https://jellyfin.example/JellySin/Lastfm/Callback");

    public CoreFixture()
    {
        Store = new FileStateStore(DirectoryPath);
        Credentials = new ApplicationCredentialService(Store, Protection);
        Accounts = new AccountService(Store, Client, Credentials, Protection, Clock);
        LibraryRules = new LibraryRuleService(Store);
    }

    public async Task ConnectAsync(Guid? user = null)
    {
        await ConfigureApplicationAsync();
        await ApproveAsync(user ?? UserId);
        await Accounts.FinishAsync(user ?? UserId, TestContext.Current.CancellationToken);
    }

    public Task ConfigureApplicationAsync()
        => Credentials.SetAsync(new ApplicationCredentials(new string('a', 32), new string('b', 32)), TestContext.Current.CancellationToken);

    /// <summary>Starts a sign-in and returns from Last.fm in the same browser, as the callback address would see it.</summary>
    public async Task<ConnectionAttempt> ApproveAsync(Guid user, string token = "browser-test-token")
    {
        var attempt = await Accounts.BeginAsync(user, Callback, false, TestContext.Current.CancellationToken);
        var approval = await Accounts.ApproveAsync(attempt.BrowserBinding, AttemptOf(attempt), token, TestContext.Current.CancellationToken);
        Assert.True(approval.Accepted);
        return attempt;
    }

    /// <summary>The attempt named in the callback address that Last.fm is asked to return to.</summary>
    public static string AttemptOf(ConnectionAttempt attempt) => AttemptOf(attempt.AuthorizationUrl);

    public static string AttemptOf(string authorizationUrl)
    {
        var callback = HttpUtility.ParseQueryString(new Uri(authorizationUrl).Query)["cb"]!;
        return HttpUtility.ParseQueryString(new Uri(callback).Query)["attempt"]!;
    }

    public void Dispose()
    {
        LibraryRules.Dispose();
        Accounts.Dispose();
        Store.Dispose();
        Directory.Delete(DirectoryPath, true);
    }
}

public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _utc = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
    private long _timestamp;
    public override DateTimeOffset GetUtcNow() => _utc;
    public override long GetTimestamp() => _timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public void Advance(double seconds) { _utc = _utc.AddSeconds(seconds); _timestamp += TimeSpan.FromSeconds(seconds).Ticks; }
}

public sealed class StubClient : ILastfmClient
{
    public Func<string, IReadOnlyDictionary<string, string>, string?, JsonDocument>? Handler { get; set; }
    public Func<string, IReadOnlyDictionary<string, string>, CancellationToken, Task<JsonDocument>>? AsyncHandler { get; set; }
    public List<(string Method, IReadOnlyDictionary<string, string> Values, string? Session)> Calls { get; } = [];
    public async Task<JsonDocument> CallAsync(string method, IReadOnlyDictionary<string, string> parameters, string? sessionKey, RequestPriority priority, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add((method, parameters, sessionKey));
        if (AsyncHandler is not null) return await AsyncHandler(method, parameters, cancellationToken);
        return Handler?.Invoke(method, parameters, sessionKey) ?? JsonDocument.Parse(method switch
        {
            "auth.getSession" => "{\"session\":{\"name\":\"TestListener\",\"key\":\"private-session-value\"}}",
            "track.scrobble" => "{\"scrobbles\":{\"scrobble\":{\"ignoredMessage\":{\"code\":\"0\"}}}}",
            _ => "{}"
        });
    }
}
