using Jellyfin.Data.Queries;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Model.Devices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace JellySin.Plugin.Lastfm.Api;

public sealed record UserDeviceView(string Id, string Name, string? AppName, bool IsExcluded);

public sealed record UpdateUserDevicesRequest(IReadOnlyList<string>? ExcludedDevices);

[Route("JellySin/Lastfm/Me/Devices")]
public sealed class UserDevicesController(
    IDeviceManager deviceManager,
    Playback.LibraryRuleService libraryRules,
    ILogger<UserDevicesController>? logger = null) : PrivateController
{
    private readonly ILogger<UserDevicesController> _logger = logger ?? NullLogger<UserDevicesController>.Instance;

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserDeviceView>>> Get(CancellationToken cancellationToken)
    {
        try
        {
            var excluded = new HashSet<string>(
                await libraryRules.GetUserExcludedDevicesAsync(CallerId, cancellationToken).ConfigureAwait(false),
                StringComparer.Ordinal);
            return Ok(CallerDevices()
                .Select(d => new UserDeviceView(
                    d.Id!,
                    !string.IsNullOrWhiteSpace(d.CustomName) ? d.CustomName : (!string.IsNullOrWhiteSpace(d.Name) ? d.Name : d.Id!),
                    d.AppName,
                    excluded.Contains(d.Id!)))
                .ToArray());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning("Failed to retrieve devices ({ErrorType}).", exception.GetType().Name);
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    [HttpPut]
    public async Task<ActionResult<IReadOnlyList<UserDeviceView>>> Update(
        UpdateUserDevicesRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var own = new HashSet<string>(CallerDevices().Select(d => d.Id!), StringComparer.Ordinal);
            var filtered = (request.ExcludedDevices ?? [])
                .Where(id => !string.IsNullOrEmpty(id) && id.Length <= Playback.LibraryRuleService.MaxDeviceIdLength && own.Contains(id))
                .Distinct(StringComparer.Ordinal)
                .Take(Playback.LibraryRuleService.MaxExcludedDevices)
                .ToArray();

            await libraryRules.SetUserExcludedDevicesAsync(CallerId, filtered, cancellationToken).ConfigureAwait(false);
            return await Get(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning("Failed to update devices ({ErrorType}).", exception.GetType().Name);
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }

    // Devices the caller signed in on. GetDevicesForUser is not used: it lists every device the
    // user may access, which with the "all devices" permission includes other users' devices.
    private IReadOnlyList<DeviceInfo> CallerDevices() =>
        (deviceManager.GetDeviceInfos(new DeviceQuery { UserId = CallerId })?.Items ?? [])
            .Where(d => !string.IsNullOrEmpty(d.Id) && d.Id.Length <= Playback.LibraryRuleService.MaxDeviceIdLength)
            .OrderByDescending(d => d.DateLastActivity ?? DateTime.MinValue)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .DistinctBy(d => d.Id, StringComparer.Ordinal)
            .Take(Playback.LibraryRuleService.MaxExcludedDevices)
            .ToArray();
}
