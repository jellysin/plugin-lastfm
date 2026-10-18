using System.Text;
using System.Text.Json;
using JellySin.Plugin.Lastfm.Api;
using JellySin.Plugin.Lastfm.Playback;
using JellySin.Plugin.Lastfm.Storage;
using JellySin.Plugin.Lastfm.Tests.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace JellySin.Plugin.Lastfm.Tests.Core;

public sealed class ScrobbleLogTests
{
    [Fact]
    public async Task LogOffByDefault_DeliveredScrobblesDoNotWriteLogKey()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var outbox = NewOutbox(fixture);
        await outbox.EnqueueAsync(Listen(fixture), TestContext.Current.CancellationToken);
        var outcome = await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.NotNull(outcome);
        Assert.Equal(1, outcome.Accepted);

        var log = await fixture.Store.ReadAsync<List<ScrobbleLogEntry>>(fixture.UserId, ScrobbleOutbox.LogStoreKey, TestContext.Current.CancellationToken);
        Assert.Null(log);
    }

    [Fact]
    public async Task LogOn_DeliveredScrobblesAppearWithCorrectFieldsAndCodes_DuplicateNotDoubled()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        await fixture.LibraryRules.SetScrobbleLogEnabledAsync(fixture.UserId, true, TestContext.Current.CancellationToken);

        var outbox = NewOutbox(fixture);
        var listen = Listen(fixture, artist: "Test Artist", title: "Test Song", album: "Test Album", mbid: Guid.NewGuid().ToString());
        await outbox.EnqueueAsync(listen, TestContext.Current.CancellationToken);
        var outcome = await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.NotNull(outcome);
        Assert.Equal(1, outcome.Accepted);

        var log = await fixture.Store.ReadAsync<List<ScrobbleLogEntry>>(fixture.UserId, ScrobbleOutbox.LogStoreKey, TestContext.Current.CancellationToken);
        Assert.NotNull(log);
        var entry = Assert.Single(log);
        Assert.Equal(listen.OccurrenceId, entry.OccurrenceId);
        Assert.Equal("Test Artist", entry.Artist);
        Assert.Equal("Test Song", entry.Title);
        Assert.Equal("Test Album", entry.Album);
        Assert.Equal(listen.Track.MusicBrainzId, entry.MusicBrainzId);
        Assert.Equal(0, entry.ResultCode);
        Assert.Equal(listen.StartedAt, entry.StartedAt);

        // Verify duplicate occurrence id is not doubled
        await outbox.EnqueueAsync(listen, TestContext.Current.CancellationToken);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        var logAfter = await fixture.Store.ReadAsync<List<ScrobbleLogEntry>>(fixture.UserId, ScrobbleOutbox.LogStoreKey, TestContext.Current.CancellationToken);
        Assert.NotNull(logAfter);
        Assert.Single(logAfter);
    }

    [Fact]
    public async Task Cap_ExceedingMaxEntriesTrimsOldest()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        await fixture.LibraryRules.SetScrobbleLogEnabledAsync(fixture.UserId, true, TestContext.Current.CancellationToken);

        var existing = new List<ScrobbleLogEntry>(ScrobbleOutbox.MaxLogEntries);
        var oldestId = Guid.NewGuid();
        var secondOldestId = Guid.NewGuid();
        existing.Add(new ScrobbleLogEntry(oldestId, fixture.Clock.GetUtcNow(), fixture.Clock.GetUtcNow(), "Oldest", "Song 0", null, null, null, 0));
        existing.Add(new ScrobbleLogEntry(secondOldestId, fixture.Clock.GetUtcNow(), fixture.Clock.GetUtcNow(), "Second", "Song 1", null, null, null, 0));
        for (var i = 2; i < ScrobbleOutbox.MaxLogEntries; i++)
        {
            existing.Add(new ScrobbleLogEntry(Guid.NewGuid(), fixture.Clock.GetUtcNow(), fixture.Clock.GetUtcNow(), $"Artist {i}", $"Song {i}", null, null, null, 0));
        }
        await fixture.Store.WriteAsync(fixture.UserId, ScrobbleOutbox.LogStoreKey, existing, TestContext.Current.CancellationToken);

        var outbox = NewOutbox(fixture);
        var newListen = Listen(fixture, artist: "Newest Artist", title: "Newest Song");
        await outbox.EnqueueAsync(newListen, TestContext.Current.CancellationToken);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);

        var log = await fixture.Store.ReadAsync<List<ScrobbleLogEntry>>(fixture.UserId, ScrobbleOutbox.LogStoreKey, TestContext.Current.CancellationToken);
        Assert.NotNull(log);
        Assert.Equal(ScrobbleOutbox.MaxLogEntries, log.Count);
        Assert.DoesNotContain(log, e => e.OccurrenceId == oldestId);
        Assert.Equal(secondOldestId, log[0].OccurrenceId);
        Assert.Equal(newListen.OccurrenceId, log[^1].OccurrenceId);
    }

    [Fact]
    public async Task UserIsolation_UserBExportShowsNoneOfUserAEntries()
    {
        using var fixture = new CoreFixture();
        var userA = fixture.UserId;
        var userB = Guid.NewGuid();

        await fixture.ConnectAsync(userA);
        await fixture.ConnectAsync(userB);
        await fixture.LibraryRules.SetScrobbleLogEnabledAsync(userA, true, TestContext.Current.CancellationToken);
        await fixture.LibraryRules.SetScrobbleLogEnabledAsync(userB, true, TestContext.Current.CancellationToken);

        var outbox = NewOutbox(fixture);
        await outbox.EnqueueAsync(Listen(fixture, userId: userA, artist: "Artist A", title: "Song A"), TestContext.Current.CancellationToken);
        await outbox.FlushAsync(userA, TestContext.Current.CancellationToken);

        var controllerA = Attach(new ScrobbleLogController(fixture.LibraryRules, fixture.Store), userA);
        var controllerB = Attach(new ScrobbleLogController(fixture.LibraryRules, fixture.Store), userB);

        var exportA = Assert.IsType<FileContentResult>(await controllerA.Export("json", TestContext.Current.CancellationToken));
        var entriesA = JsonSerializer.Deserialize<List<ScrobbleLogEntry>>(exportA.FileContents, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(entriesA);
        Assert.Single(entriesA, e => e.Artist == "Artist A");

        var exportB = Assert.IsType<FileContentResult>(await controllerB.Export("json", TestContext.Current.CancellationToken));
        var entriesB = JsonSerializer.Deserialize<List<ScrobbleLogEntry>>(exportB.FileContents, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(entriesB);
        Assert.Empty(entriesB);
    }

    [Fact]
    public async Task UserRemovedOrDisconnected_LogKeyGone()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        await fixture.LibraryRules.SetScrobbleLogEnabledAsync(fixture.UserId, true, TestContext.Current.CancellationToken);

        var outbox = NewOutbox(fixture);
        await outbox.EnqueueAsync(Listen(fixture), TestContext.Current.CancellationToken);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);

        var logBefore = await fixture.Store.ReadAsync<List<ScrobbleLogEntry>>(fixture.UserId, ScrobbleOutbox.LogStoreKey, TestContext.Current.CancellationToken);
        Assert.NotNull(logBefore);

        await fixture.Accounts.DisconnectAsync(fixture.UserId, TestContext.Current.CancellationToken);
        var logAfterDisconnect = await fixture.Store.ReadAsync<List<ScrobbleLogEntry>>(fixture.UserId, ScrobbleOutbox.LogStoreKey, TestContext.Current.CancellationToken);
        Assert.Null(logAfterDisconnect);
    }

    [Fact]
    public async Task ReconnectAfterDisconnect_LogStaysOffUntilOptInAgain()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        await fixture.LibraryRules.SetScrobbleLogEnabledAsync(fixture.UserId, true, TestContext.Current.CancellationToken);
        Assert.True(await fixture.LibraryRules.IsScrobbleLogEnabledAsync(fixture.UserId, TestContext.Current.CancellationToken));

        await fixture.Accounts.DisconnectAsync(fixture.UserId, TestContext.Current.CancellationToken);
        await fixture.ConnectAsync();
        Assert.False(await fixture.LibraryRules.IsScrobbleLogEnabledAsync(fixture.UserId, TestContext.Current.CancellationToken));

        var outbox = NewOutbox(fixture);
        await outbox.EnqueueAsync(Listen(fixture), TestContext.Current.CancellationToken);
        var outcome = await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.NotNull(outcome);
        Assert.Equal(1, outcome.Accepted);
        Assert.Null(await fixture.Store.ReadAsync<List<ScrobbleLogEntry>>(fixture.UserId, ScrobbleOutbox.LogStoreKey, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SwitchingOff_DeletesTheLog()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        await fixture.LibraryRules.SetScrobbleLogEnabledAsync(fixture.UserId, true, TestContext.Current.CancellationToken);

        var outbox = NewOutbox(fixture);
        await outbox.EnqueueAsync(Listen(fixture), TestContext.Current.CancellationToken);
        await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);

        var logBefore = await fixture.Store.ReadAsync<List<ScrobbleLogEntry>>(fixture.UserId, ScrobbleOutbox.LogStoreKey, TestContext.Current.CancellationToken);
        Assert.NotNull(logBefore);

        await fixture.LibraryRules.SetScrobbleLogEnabledAsync(fixture.UserId, false, TestContext.Current.CancellationToken);
        var logAfter = await fixture.Store.ReadAsync<List<ScrobbleLogEntry>>(fixture.UserId, ScrobbleOutbox.LogStoreKey, TestContext.Current.CancellationToken);
        Assert.Null(logAfter);
        Assert.False(await fixture.LibraryRules.IsScrobbleLogEnabledAsync(fixture.UserId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LogWriteFailure_DoesNotBreakDelivery()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        await fixture.LibraryRules.SetScrobbleLogEnabledAsync(fixture.UserId, true, TestContext.Current.CancellationToken);

        var storeMock = new Mock<IStateStore>();
        storeMock.Setup(s => s.ReadAsync<OutboxState>(fixture.UserId, "outbox", It.IsAny<CancellationToken>()))
            .Returns(() => fixture.Store.ReadAsync<OutboxState>(fixture.UserId, "outbox", TestContext.Current.CancellationToken));
        storeMock.Setup(s => s.UpdateAsync<OutboxState>(fixture.UserId, "outbox", It.IsAny<Func<OutboxState?, OutboxState>>(), It.IsAny<CancellationToken>()))
            .Returns((Guid uid, string key, Func<OutboxState?, OutboxState> update, CancellationToken ct) => fixture.Store.UpdateAsync(uid, key, update, ct));
        storeMock.Setup(s => s.UpdateAsync<List<ScrobbleLogEntry>>(fixture.UserId, ScrobbleOutbox.LogStoreKey, It.IsAny<Func<List<ScrobbleLogEntry>?, List<ScrobbleLogEntry>>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Simulated disk error during log write."));

        var outbox = new ScrobbleOutbox(storeMock.Object, fixture.Accounts, fixture.Client, fixture.Clock, fixture.LibraryRules);
        await outbox.EnqueueAsync(Listen(fixture), TestContext.Current.CancellationToken);

        var outcome = await outbox.FlushAsync(fixture.UserId, TestContext.Current.CancellationToken);
        Assert.NotNull(outcome);
        Assert.Equal(1, outcome.Accepted);
        Assert.Null(outcome.ErrorCode);
    }

    [Fact]
    public async Task Export_CsvHeaderQuotingFormulaPrefix_JsonShape_UnknownFormat400()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        await fixture.LibraryRules.SetScrobbleLogEnabledAsync(fixture.UserId, true, TestContext.Current.CancellationToken);

        var entries = new List<ScrobbleLogEntry>
        {
            new(Guid.NewGuid(), fixture.Clock.GetUtcNow(), fixture.Clock.GetUtcNow(), "=FormulaArtist", "+PlusSong", "-MinusAlbum", "@AtAlbumArtist", null, 0),
            new(Guid.NewGuid(), fixture.Clock.GetUtcNow(), fixture.Clock.GetUtcNow(), "Normal Artist", "Comma, and \"Quotes\"", "\tTabAlbum", null, null, 1)
        };
        await fixture.Store.WriteAsync(fixture.UserId, ScrobbleOutbox.LogStoreKey, entries, TestContext.Current.CancellationToken);

        var controller = Attach(new ScrobbleLogController(fixture.LibraryRules, fixture.Store), fixture.UserId);

        // CSV Export check
        var csvResult = Assert.IsType<FileContentResult>(await controller.Export("csv", TestContext.Current.CancellationToken));
        Assert.Equal("text/csv; charset=utf-8", csvResult.ContentType);
        Assert.Equal("scrobbles.csv", csvResult.FileDownloadName);
        var csvText = Encoding.UTF8.GetString(csvResult.FileContents);
        var lines = csvText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("OccurrenceId,CompletedAt,StartedAt,Artist,Title,Album,AlbumArtist,MusicBrainzId,ResultCode", lines[0]);
        Assert.Contains("'=FormulaArtist", lines[1]);
        Assert.Contains("'+PlusSong", lines[1]);
        Assert.Contains("'-MinusAlbum", lines[1]);
        Assert.Contains("'@AtAlbumArtist", lines[1]);
        Assert.Contains("\"Comma, and \"\"Quotes\"\"\"", lines[2]);
        Assert.Contains(",'\tTabAlbum,", lines[2]);

        // JSON Export check
        var jsonResult = Assert.IsType<FileContentResult>(await controller.Export("json", TestContext.Current.CancellationToken));
        Assert.Equal("application/json; charset=utf-8", jsonResult.ContentType);
        Assert.Equal("scrobbles.json", jsonResult.FileDownloadName);
        var jsonText = Encoding.UTF8.GetString(jsonResult.FileContents);
        var jsonDoc = JsonDocument.Parse(jsonText);
        Assert.Equal(2, jsonDoc.RootElement.GetArrayLength());
        var firstJson = jsonDoc.RootElement[0];
        Assert.Equal("=FormulaArtist", firstJson.GetProperty("artist").GetString());
        Assert.Equal(0, firstJson.GetProperty("resultCode").GetInt32());

        // Unknown format check
        Assert.IsType<BadRequestObjectResult>(await controller.Export("yaml", TestContext.Current.CancellationToken));
        Assert.IsType<BadRequestObjectResult>(await controller.Export(null, TestContext.Current.CancellationToken));
        Assert.IsType<BadRequestObjectResult>(await controller.Export("", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ControllerSettings_GetAndSet()
    {
        using var fixture = new CoreFixture();
        await fixture.ConnectAsync();
        var controller = Attach(new ScrobbleLogController(fixture.LibraryRules, fixture.Store), fixture.UserId);

        var initial = Assert.IsType<OkObjectResult>((await controller.GetSettings(TestContext.Current.CancellationToken)).Result);
        var initialSettings = Assert.IsType<ScrobbleLogSettingsResponse>(initial.Value);
        Assert.False(initialSettings.Enabled);
        Assert.Equal(0, initialSettings.Count);

        var updated = Assert.IsType<OkObjectResult>((await controller.UpdateSettings(new UpdateScrobbleLogSettingsRequest(true), TestContext.Current.CancellationToken)).Result);
        var updatedSettings = Assert.IsType<ScrobbleLogSettingsResponse>(updated.Value);
        Assert.True(updatedSettings.Enabled);

        var fetched = Assert.IsType<OkObjectResult>((await controller.GetSettings(TestContext.Current.CancellationToken)).Result);
        var fetchedSettings = Assert.IsType<ScrobbleLogSettingsResponse>(fetched.Value);
        Assert.True(fetchedSettings.Enabled);
    }

    private static ScrobbleOutbox NewOutbox(CoreFixture fixture) =>
        new(fixture.Store, fixture.Accounts, fixture.Client, fixture.Clock, fixture.LibraryRules);

    private static EligibleListen Listen(CoreFixture fixture, Guid? userId = null, string artist = "Artist", string title = "Song", string? album = "Album", string? albumArtist = null, string? mbid = null)
    {
        var targetUser = userId ?? fixture.UserId;
        return new EligibleListen(
            Guid.NewGuid(),
            targetUser,
            new MusicTrack(Guid.NewGuid(), artist, title, album, mbid, 180, AlbumArtist: albumArtist),
            fixture.Clock.GetUtcNow(),
            fixture.Accounts.GetCaptureBinding(targetUser)?.AccountGeneration ?? Guid.Empty,
            fixture.Accounts.GetCaptureBinding(targetUser)?.CaptureGeneration ?? Guid.Empty);
    }

    private static T Attach<T>(T controller, Guid userId) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
        controller.HttpContext.Items[typeof(CallerFilter)] = CallerSecurityTests.Identity(userId);
        return controller;
    }
}
