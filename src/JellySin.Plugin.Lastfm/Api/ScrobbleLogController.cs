using System.Globalization;
using System.Text;
using System.Text.Json;
using JellySin.Plugin.Lastfm.Playback;
using JellySin.Plugin.Lastfm.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellySin.Plugin.Lastfm.Api;

public sealed record ScrobbleLogSettingsResponse(bool Enabled, int Count);

public sealed record UpdateScrobbleLogSettingsRequest(bool Enabled);

[Route("JellySin/Lastfm/Me/ScrobbleLog")]
public sealed class ScrobbleLogController(
    LibraryRuleService libraryRules,
    IStateStore store,
    ILogger<ScrobbleLogController>? logger = null) : PrivateController
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ILogger<ScrobbleLogController> _logger = logger ?? NullLogger<ScrobbleLogController>.Instance;

    [HttpGet("settings")]
    public async Task<ActionResult<ScrobbleLogSettingsResponse>> GetSettings(CancellationToken cancellationToken)
    {
        var enabled = await libraryRules.IsScrobbleLogEnabledAsync(CallerId, cancellationToken).ConfigureAwait(false);
        var count = 0;
        if (enabled)
        {
            var log = await store.ReadAsync<List<ScrobbleLogEntry>>(CallerId, ScrobbleOutbox.LogStoreKey, cancellationToken).ConfigureAwait(false);
            count = log?.Count ?? 0;
        }
        return Ok(new ScrobbleLogSettingsResponse(enabled, count));
    }

    [HttpPost("settings")]
    [HttpPut("settings")]
    public async Task<ActionResult<ScrobbleLogSettingsResponse>> UpdateSettings(
        [FromBody] UpdateScrobbleLogSettingsRequest request,
        CancellationToken cancellationToken)
    {
        if (request is null) return BadRequest("Request body is required.");
        await libraryRules.SetScrobbleLogEnabledAsync(CallerId, request.Enabled, cancellationToken).ConfigureAwait(false);
        var count = 0;
        if (request.Enabled)
        {
            var log = await store.ReadAsync<List<ScrobbleLogEntry>>(CallerId, ScrobbleOutbox.LogStoreKey, cancellationToken).ConfigureAwait(false);
            count = log?.Count ?? 0;
        }
        return Ok(new ScrobbleLogSettingsResponse(request.Enabled, count));
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] string? format, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(format) || (!format.Equals("json", StringComparison.OrdinalIgnoreCase) && !format.Equals("csv", StringComparison.OrdinalIgnoreCase)))
        {
            return BadRequest("Invalid format. Expected 'json' or 'csv'.");
        }

        var log = await store.ReadAsync<List<ScrobbleLogEntry>>(CallerId, ScrobbleOutbox.LogStoreKey, cancellationToken).ConfigureAwait(false) ?? [];
        Response.Headers["X-Content-Type-Options"] = "nosniff";

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(log, JsonOptions);
            return File(bytes, "application/json; charset=utf-8", "scrobbles.json");
        }

        var csv = BuildCsv(log);
        return File(Encoding.UTF8.GetBytes(csv), "text/csv; charset=utf-8", "scrobbles.csv");
    }

    public static string BuildCsv(IReadOnlyList<ScrobbleLogEntry> entries)
    {
        var builder = new StringBuilder();
        builder.Append("OccurrenceId,CompletedAt,StartedAt,Artist,Title,Album,AlbumArtist,MusicBrainzId,ResultCode\r\n");
        foreach (var entry in entries)
        {
            builder.Append(entry.OccurrenceId).Append(',')
                .Append(entry.CompletedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append(',')
                .Append(entry.StartedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append(',')
                .Append(EscapeCsvCell(entry.Artist)).Append(',')
                .Append(EscapeCsvCell(entry.Title)).Append(',')
                .Append(EscapeCsvCell(entry.Album)).Append(',')
                .Append(EscapeCsvCell(entry.AlbumArtist)).Append(',')
                .Append(EscapeCsvCell(entry.MusicBrainzId)).Append(',')
                .Append(entry.ResultCode.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        }
        return builder.ToString();
    }

    public static string EscapeCsvCell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var text = value;
        if (text[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            text = "'" + text;
        }
        if (text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r'))
        {
            return $"\"{text.Replace("\"", "\"\"")}\"";
        }
        return text;
    }
}
