using System.Net;
using System.Text.Json;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Transport;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace JellySin.Plugin.Lastfm.Metadata;

public sealed class LastfmArtistProvider(
    ILastfmClient client,
    ApplicationCredentialService credentials) : IRemoteMetadataProvider<MusicArtist, ArtistInfo>
{
    public string Name => "Last.fm";

    public async Task<MetadataResult<MusicArtist>> GetMetadata(ArtistInfo info, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var app = await credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!app.IsConfigured || Plugin.Instance?.Configuration.Enabled == false)
            return new MetadataResult<MusicArtist>();

        var (parameters, queriedById) = BuildParameters(info);
        if (parameters is null)
            return new MetadataResult<MusicArtist>();

        try
        {
            using var document = await client.CallAsync("artist.getInfo", parameters, null, RequestPriority.Background, cancellationToken).ConfigureAwait(false);
            return Parse(document, info, queriedById);
        }
        catch (LastfmException ex) when (ex.Code is 6)
        {
            return new MetadataResult<MusicArtist>();
        }
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(ArtistInfo searchInfo, CancellationToken cancellationToken)
    {
        var metadata = await GetMetadata(searchInfo, cancellationToken).ConfigureAwait(false);
        if (!metadata.HasMetadata || metadata.Item is null)
            return [];

        var result = new RemoteSearchResult
        {
            Name = metadata.Item.Name,
            SearchProviderName = Name,
            Overview = metadata.Item.Overview,
            ProviderIds = new Dictionary<string, string>(metadata.Item.ProviderIds, StringComparer.OrdinalIgnoreCase)
        };
        return [result];
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private static (Dictionary<string, string>? Parameters, bool QueriedById) BuildParameters(ArtistInfo info)
    {
        if (info.ProviderIds is not null && info.ProviderIds.TryGetValue("MusicBrainzArtist", out var mbid) && !string.IsNullOrWhiteSpace(mbid))
        {
            return (new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mbid"] = mbid.Trim(),
                ["autocorrect"] = "0"
            }, true);
        }

        if (!string.IsNullOrWhiteSpace(info.Name))
        {
            return (new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["artist"] = info.Name,
                ["autocorrect"] = "0"
            }, false);
        }

        return (null, false);
    }

    private static MetadataResult<MusicArtist> Parse(JsonDocument document, ArtistInfo info, bool queriedById)
    {
        var artistNode = MusicJson.Child(document.RootElement, "artist");
        if (artistNode.ValueKind != JsonValueKind.Object)
            return new MetadataResult<MusicArtist>();

        var bioNode = MusicJson.Child(artistNode, "bio");
        var overview = MusicJson.CleanWiki(bioNode);
        if (string.IsNullOrWhiteSpace(overview))
            return new MetadataResult<MusicArtist> { HasMetadata = false };

        var name = MusicJson.Text(artistNode, "name");
        var item = new MusicArtist
        {
            Name = string.IsNullOrWhiteSpace(name) ? info.Name : name,
            Overview = overview
        };

        return new MetadataResult<MusicArtist>
        {
            HasMetadata = true,
            Item = item,
            QueriedById = queriedById
        };
    }
}
