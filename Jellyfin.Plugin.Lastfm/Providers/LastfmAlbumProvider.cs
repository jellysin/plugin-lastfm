using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Lastfm.Providers;

public class LastfmAlbumProvider : IRemoteMetadataProvider<MusicAlbum, AlbumInfo>, IHasOrder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServerConfigurationManager _config;
    private readonly ILogger<LastfmAlbumProvider> _logger;

    public LastfmAlbumProvider(IHttpClientFactory httpClientFactory, IServerConfigurationManager config, ILoggerFactory loggerFactory)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = loggerFactory.CreateLogger<LastfmAlbumProvider>();
    }

    public string Name => "last.fm";
    public int Order => 2;
    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(AlbumInfo searchInfo, CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<RemoteSearchResult>>([]);

    public async Task<MetadataResult<MusicAlbum>> GetMetadata(AlbumInfo id, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<MusicAlbum>();
        var data = await GetAlbum(id, cancellationToken).ConfigureAwait(false);
        if (data?.album is not { } album)
        {
            return result;
        }
        var item = new MusicAlbum { Overview = album.wiki?.content };
        if (DateTime.TryParse(album.releasedate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var release)
            && release.Year > 1901)
        {
            item.PremiereDate = release;
            item.ProductionYear = release.Year;
        }
        result.Item = item;
        result.HasMetadata = true;
        var mbid = id.GetReleaseId() ?? id.GetReleaseGroupId();
        var image = LastfmHelper.GetImageUrl(album, out var size);
        if (!string.IsNullOrEmpty(mbid) && !string.IsNullOrEmpty(image))
        {
            try
            {
                LastfmHelper.SaveImageInfo(_config.ApplicationPaths, mbid, image, size);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogWarning("Unable to cache Last.fm album image ({FailureType})", exception.GetType().Name);
            }
        }
        return result;
    }

    private async Task<LastfmGetAlbumResult?> GetAlbum(AlbumInfo item, CancellationToken cancellationToken)
    {
        foreach (var id in new[] { item.GetReleaseId(), item.GetReleaseGroupId() }.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct())
        {
            var result = await Fetch("mbid=" + WebUtility.UrlEncode(id), cancellationToken).ConfigureAwait(false);
            if (result?.album is not null)
            {
                return result;
            }
        }
        var candidates = item.SongInfos.Select(song => (Artist: song.AlbumArtists?.FirstOrDefault(), Album: song.Album))
            .Append((Artist: item.GetAlbumArtist(), Album: item.Name))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Artist) && !string.IsNullOrWhiteSpace(pair.Album))
            .Distinct();
        foreach (var candidate in candidates)
        {
            var result = await Fetch("artist=" + WebUtility.UrlEncode(candidate.Artist) + "&album=" + WebUtility.UrlEncode(candidate.Album), cancellationToken).ConfigureAwait(false);
            if (result?.album is not null)
            {
                return result;
            }
        }
        return null;
    }

    private Task<LastfmGetAlbumResult?> Fetch(string query, CancellationToken cancellationToken)
        => LastfmMetadataClient.Get<LastfmGetAlbumResult>(_httpClientFactory, _logger,
            LastfmArtistProvider.RootUrl + "method=album.getInfo&" + query + "&api_key=" + LastfmArtistProvider.ApiKey + "&format=json", cancellationToken);

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => LastfmMetadataClient.GetImage(_httpClientFactory, url, cancellationToken);
}
