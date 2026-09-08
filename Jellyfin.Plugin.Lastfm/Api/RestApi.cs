using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lastfm.Models.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lastfm.Api;

[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Lastfm/Login")]
public class RestApi : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RestApi> _logger;

    public RestApi(IHttpClientFactory httpClientFactory, ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _logger = loggerFactory.CreateLogger<RestApi>();
    }

    [HttpPost]
    [Consumes("application/json")]
    public async Task<ActionResult<MobileSessionResponse>> CreateMobileSession(
        [FromBody] LastFMUser lastFMUser, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(lastFMUser.Username) || string.IsNullOrEmpty(lastFMUser.Password))
        {
            return BadRequest(new MobileSessionResponse { ErrorCode = 6, Message = "Username and password are required." });
        }
        using var client = new LastfmApiClient(_httpClientFactory, _logger);
        var response = await client.RequestSession(lastFMUser.Username, lastFMUser.Password, cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return StatusCode(502, new MobileSessionResponse { ErrorCode = 11, Message = "Last.fm is unavailable. Please try again." });
        }
        if (response.IsError() || string.IsNullOrEmpty(response.Session?.Key) || string.IsNullOrEmpty(response.Session.Name))
        {
            return new MobileSessionResponse { ErrorCode = response.ErrorCode > 0 ? response.ErrorCode : 4, Message = "Last.fm login failed. Check your credentials and try again." };
        }
        return response;
    }
}

public class LastFMUser
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}
