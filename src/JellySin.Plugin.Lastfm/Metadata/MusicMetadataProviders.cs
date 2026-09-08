using System.Net;
using JellySin.Plugin.Lastfm.Features;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace JellySin.Plugin.Lastfm.Metadata;

public abstract class MusicMetadataProvider<TItem, TInfo>(MetadataApi api) : IRemoteMetadataProvider<TItem, TInfo>
    where TItem : BaseItem, IHasLookupInfo<TInfo>, new()
    where TInfo : ItemLookupInfo, new()
{
    public string Name => "JellySin Last.fm";
    protected abstract string Kind { get; }
    protected abstract string ProviderId { get; }
    protected abstract string? Artist(TInfo info);

    public async Task<MetadataResult<TItem>> GetMetadata(TInfo info, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<TItem>();
        if (Plugin.Instance?.Configuration.Enabled == false || Plugin.Instance?.Configuration.MetadataEnabled == false) return result;
        var metadata = await api.GetAsync(Kind, info.Name, Artist(info), info.ProviderIds.GetValueOrDefault(ProviderId), cancellationToken).ConfigureAwait(false);
        if (metadata is null || metadata.Name.Length == 0) return result;
        await api.ReserveNativeCopyAsync(Kind + "|" + (string.IsNullOrEmpty(info.Path) ? info.Name + "|" + Artist(info) : info.Path),
            metadata, cancellationToken).ConfigureAwait(false);
        var item = new TItem { Name = metadata.Name, Overview = WebUtility.HtmlEncode(metadata.Overview), Tags = metadata.Tags };
        if (metadata.MusicBrainzId is { } mbid) item.ProviderIds[ProviderId] = mbid;
        if (metadata.Url is { } url) item.ProviderIds["JellySinLastfm"] = url;
        result.Item = item;
        result.HasMetadata = true;
        return result;
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(TInfo searchInfo, CancellationToken cancellationToken)
    {
        if (Plugin.Instance?.Configuration.Enabled == false || Plugin.Instance?.Configuration.MetadataEnabled == false) return [];
        var results = await api.SearchAsync(Kind, searchInfo.Name, Artist(searchInfo), cancellationToken).ConfigureAwait(false);
        return results.Select(metadata =>
        {
            var result = new RemoteSearchResult { Name = metadata.Name, SearchProviderName = Name };
            if (metadata.MusicBrainzId is { } id) result.ProviderIds[ProviderId] = id;
            if (metadata.Url is { } url) result.ProviderIds["JellySinLastfm"] = url;
            return result;
        }).ToArray();
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // This required search-provider member never downloads artwork: the service does not grant artwork rights.
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public sealed class ArtistMetadataProvider(MetadataApi api) : MusicMetadataProvider<MusicArtist, ArtistInfo>(api)
{
    protected override string Kind => "artist";
    protected override string ProviderId => "MusicBrainzArtist";
    protected override string? Artist(ArtistInfo info) => null;
}

public sealed class AlbumMetadataProvider(MetadataApi api) : MusicMetadataProvider<MusicAlbum, AlbumInfo>(api)
{
    protected override string Kind => "album";
    protected override string ProviderId => "MusicBrainzAlbum";
    protected override string? Artist(AlbumInfo info) => info.AlbumArtists.FirstOrDefault();
}

public sealed class TrackMetadataProvider(MetadataApi api) : MusicMetadataProvider<Audio, SongInfo>(api)
{
    protected override string Kind => "track";
    protected override string ProviderId => "MusicBrainzTrack";
    protected override string? Artist(SongInfo info) => info.Artists.FirstOrDefault();
}

public sealed class LastfmExternalUrls : IExternalUrlProvider
{
    public string Name => "Last.fm";
    public IEnumerable<string> GetExternalUrls(BaseItem item)
    {
        if (item is Audio or MusicArtist or MusicAlbum && item.ProviderIds.TryGetValue("JellySinLastfm", out var url)
            && MusicJson.SafeUrl(url) is { } safe) yield return safe;
    }
}
