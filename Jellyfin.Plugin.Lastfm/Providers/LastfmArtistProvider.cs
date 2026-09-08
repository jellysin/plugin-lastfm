using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lastfm.Providers;

public class LastfmArtistProvider : IRemoteMetadataProvider<MusicArtist, ArtistInfo>, IHasOrder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LastfmArtistProvider> _logger;
    internal const string RootUrl = "https://ws.audioscrobbler.com/2.0/?";
    internal static string ApiKey = "7b76553c3eb1d341d642755aecc40a33";

    public LastfmArtistProvider(IHttpClientFactory httpClientFactory, IServerConfigurationManager config, ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _logger = loggerFactory.CreateLogger<LastfmArtistProvider>();
    }

    public string Name => "last.fm";
    public int Order => 2;
    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(ArtistInfo searchInfo, CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<RemoteSearchResult>>([]);

    public async Task<MetadataResult<MusicArtist>> GetMetadata(ArtistInfo id, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<MusicArtist>();
        var mbid = id.GetMusicBrainzArtistId();
        if (string.IsNullOrWhiteSpace(mbid))
        {
            return result;
        }
        var data = await LastfmMetadataClient.Get<LastfmGetArtistResult>(_httpClientFactory, _logger,
            RootUrl + "method=artist.getInfo&mbid=" + WebUtility.UrlEncode(mbid) + "&api_key=" + ApiKey + "&format=json", cancellationToken).ConfigureAwait(false);
        if (data?.artist is not { } artist)
        {
            return result;
        }
        var item = new MusicArtist();
        if (artist.bio is { } bio)
        {
            item.Overview = (bio.content ?? string.Empty).StripHtml();
            if (!string.IsNullOrWhiteSpace(bio.placeformed))
            {
                item.ProductionLocations = [bio.placeformed];
            }
            if (int.TryParse(bio.yearformed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) && year is > 0 and <= 9999)
            {
                item.PremiereDate = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                item.ProductionYear = year;
            }
        }
        result.Item = item;
        result.HasMetadata = true;
        return result;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => LastfmMetadataClient.GetImage(_httpClientFactory, url, cancellationToken);
}
