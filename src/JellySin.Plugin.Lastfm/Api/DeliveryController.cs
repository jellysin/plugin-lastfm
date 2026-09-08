using JellySin.Plugin.Lastfm.Playback;
using Microsoft.AspNetCore.Mvc;

namespace JellySin.Plugin.Lastfm.Api;

[Route("JellySin/Lastfm/Me/Delivery")]
public sealed class DeliveryController(ScrobbleOutbox outbox, PlaybackService playback) : PrivateController
{
    [HttpGet]
    public async Task<object> Status(CancellationToken cancellationToken)
    {
        var personal = await outbox.GetStatusAsync(CallerId, cancellationToken).ConfigureAwait(false);
        var runtime = playback.GetStatus();
        return new
        {
            personal.Pending,
            personal.Blocked,
            personal.LastErrorCode,
            personal.LastIgnoredCode,
            personal.Rejected,
            personal.LastRejectedCode,
            PendingPersistence = playback.PendingPersistence(CallerId),
            runtime.LegacyPluginDetected,
            runtime.DroppedSnapshots,
            runtime.FailedWrites
        };
    }

    [HttpPost("Retry")]
    public async Task<IActionResult> Retry(CancellationToken cancellationToken)
    {
        await outbox.ResumeAsync(CallerId, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}
