using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Lastfm.Providers;

public class LastfmImageProvider : IRemoteImageProvider, IHasOrder
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IServerConfigurationManager _config;

    public LastfmImageProvider(IHttpClientFactory httpClientFactory, IServerConfigurationManager config)
    {
        _httpClientFactory = httpClientFactory;
        _config = config;
    }

    public string Name => ProviderName;
    public static string ProviderName => "last.fm";
    public int Order => 3;
    public bool Supports(BaseItem item) => item is MusicAlbum;
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => [ImageType.Primary];

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        foreach (var provider in new[] { MetadataProvider.MusicBrainzAlbum, MetadataProvider.MusicBrainzReleaseGroup })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = LastfmHelper.GetImageCachePath(_config.ApplicationPaths, item.GetProviderId(provider));
            if (path is null)
            {
                continue;
            }
            try
            {
                var contents = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                var url = contents.Split('|')[0];
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                {
                    return [new RemoteImageInfo { ProviderName = Name, Url = url, Type = ImageType.Primary }];
                }
            }
            catch (IOException)
            {
                // A missing or incomplete cache can be repopulated by the metadata provider.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        return [];
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
        => LastfmMetadataClient.GetImage(_httpClientFactory, url, cancellationToken);
}
