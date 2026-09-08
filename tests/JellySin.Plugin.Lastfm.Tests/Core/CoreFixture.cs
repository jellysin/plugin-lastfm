using System.Text.Json;
using JellySin.Plugin.Lastfm.Configuration;
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
    public Guid UserId { get; } = Guid.NewGuid();

    public CoreFixture()
    {
        Store = new FileStateStore(DirectoryPath);
        Credentials = new ApplicationCredentialService(Store, Protection);
        Accounts = new AccountService(Store, Client, Credentials, Protection, Clock);
    }

    public async Task ConnectAsync(Guid? user = null)
    {
        await Credentials.SetAsync(new ApplicationCredentials(new string('a', 32), new string('b', 32)), TestContext.Current.CancellationToken);
        var attempt = await Accounts.BeginAsync(user ?? UserId, TestContext.Current.CancellationToken);
        await Accounts.FinishAsync(user ?? UserId, attempt.AttemptId, TestContext.Current.CancellationToken);
    }

    public void Dispose()
    {
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
            "auth.getToken" => "{\"token\":\"browser-test-token\"}",
            "auth.getSession" => "{\"session\":{\"name\":\"TestListener\",\"key\":\"private-session-value\"}}",
            "track.scrobble" => "{\"scrobbles\":{\"scrobble\":{\"ignoredMessage\":{\"code\":\"0\"}}}}",
            _ => "{}"
        });
    }
}
