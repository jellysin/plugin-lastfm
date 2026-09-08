using System.ComponentModel.DataAnnotations;
using JellySin.Plugin.Lastfm.Features;
using Microsoft.AspNetCore.Mvc;

namespace JellySin.Plugin.Lastfm.Api;

[Route("JellySin/Lastfm/Me")]
public sealed class MusicController(MusicFeatureService music, DiscoveryService discovery) : PrivateController
{
    [HttpGet("History")]
    public Task<MusicPage> History([FromQuery, Range(1, 100_000)] int page = 1, [FromQuery] long? until = null,
        CancellationToken cancellationToken = default) => music.GetHistoryAsync(CallerId, page, cancellationToken, until);

    [HttpGet("Overview")]
    public Task<MusicOverview> Overview([FromQuery] string period = "1month", CancellationToken cancellationToken = default) =>
        music.GetOverviewAsync(CallerId, period, cancellationToken);

    [HttpGet("Library/Search")]
    public Task<IReadOnlyList<MusicSeed>> Seeds([FromQuery, Required, StringLength(128, MinimumLength = 2)] string query,
        CancellationToken cancellationToken) => discovery.SearchSeedsAsync(CallerId, query, cancellationToken);

    [HttpGet("Charts")]
    public Task<MusicChart> Charts([FromQuery] string period = "1month", CancellationToken cancellationToken = default) =>
        music.GetChartsAsync(CallerId, period, cancellationToken);

    [HttpPost("History/Preview")]
    public async Task<HistoryPreviewView> Preview(CancellationToken cancellationToken) =>
        HistoryPreviewView.From(await music.PreviewHistoryImportAsync(CallerId, cancellationToken).ConfigureAwait(false), 1);

    [HttpGet("History/Preview")]
    public async Task<HistoryPreviewView?> SavedPreview([FromQuery, Range(1, 500)] int page = 1,
        CancellationToken cancellationToken = default)
    {
        var preview = await music.GetHistoryImportPreviewAsync(CallerId, cancellationToken).ConfigureAwait(false);
        return preview is null ? null : HistoryPreviewView.From(preview, page);
    }

    [HttpPost("History/Continue")]
    public async Task<HistoryPreviewView> Continue(PreviewRequest request, CancellationToken cancellationToken) =>
        HistoryPreviewView.From(await music.ContinueHistoryImportAsync(CallerId, request.PreviewId, cancellationToken).ConfigureAwait(false), 1);

    [HttpPost("History/Import")]
    public async Task<IActionResult> Import(PreviewRequest request, CancellationToken cancellationToken)
    {
        await music.ApplyHistoryImportAsync(CallerId, request.PreviewId, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpGet("Discovery")]
    public Task<DiscoveryResult> Discover([FromQuery] Guid? seedItemId, CancellationToken cancellationToken) =>
        discovery.GetAsync(CallerId, seedItemId, cancellationToken);
}

public sealed record PreviewRequest(Guid PreviewId);

public sealed record HistoryPreviewView(Guid Id, DateTimeOffset ExpiresAt, IReadOnlyList<HistoryImportEntry> Entries,
    IReadOnlyList<MusicMatch> Unmatched, int MatchedCount, int UnmatchedCount, bool Complete, int NextPage, long? Until, int Page, int Pages)
{
    public static HistoryPreviewView From(HistoryImportPreview preview, int page)
    {
        var pages = Math.Max(1, (Math.Max(preview.Entries.Count, preview.Unmatched.Count) + 199) / 200);
        page = Math.Clamp(page, 1, pages);
        return new(preview.Id, preview.ExpiresAt, preview.Entries.Skip((page - 1) * 200).Take(200).ToArray(),
            preview.Unmatched.Skip((page - 1) * 200).Take(200).ToArray(), preview.Entries.Count, preview.Unmatched.Count,
            preview.Complete, preview.NextPage, preview.Until, page, pages);
    }
}
