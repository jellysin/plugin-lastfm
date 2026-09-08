using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Lastfm.Models.Requests;
using Jellyfin.Plugin.Lastfm.Models.Responses;
using Jellyfin.Plugin.Lastfm.Resources;
using Jellyfin.Plugin.Lastfm.Utils;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lastfm.Api;

public class BaseLastfmApiClient
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    public BaseLastfmApiClient(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task<TResponse?> Post<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : BaseRequest
        where TResponse : BaseResponse
    {
        var data = request.ToDictionary();
        Helpers.AppendSignature(ref data);
        var message = new HttpRequestMessage(HttpMethod.Post, GetEndpoint())
        {
            Content = new FormUrlEncodedContent(data)
        };
        return Send<TResponse>(message, cancellationToken);
    }

    public Task<TResponse?> Get<TRequest, TResponse>(TRequest request, CancellationToken cancellationToken = default)
        where TRequest : BaseRequest
        where TResponse : BaseResponse
    {
        var message = new HttpRequestMessage(HttpMethod.Get,
            GetEndpoint() + "&" + Helpers.DictionaryToQueryString(request.ToDictionary()));
        return Send<TResponse>(message, cancellationToken);
    }

    private static string GetEndpoint() => $"https://{Strings.Endpoints.LastfmApi}/2.0/?format=json";

    private async Task<TResponse?> Send<TResponse>(HttpRequestMessage message, CancellationToken cancellationToken)
        where TResponse : BaseResponse
    {
        using (message)
        using (var client = _httpClientFactory.CreateClient())
        {
            try
            {
                using var response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Last.fm request failed with HTTP status {StatusCode}", (int)response.StatusCode);
                }

                var result = await response.Content.ReadFromJsonAsync<TResponse>(SerializerOptions, cancellationToken).ConfigureAwait(false);
                if (result?.IsError() == true)
                {
                    // Remote bodies can contain credentials or request parameters.
                    _logger.LogWarning("Last.fm returned API error {ErrorCode}", result.ErrorCode);
                }
                return response.IsSuccessStatusCode || result?.IsError() == true ? result : null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or OperationCanceledException)
            {
                _logger.LogWarning("Last.fm request failed ({FailureType})", exception.GetType().Name);
                return null;
            }
        }
    }
}
