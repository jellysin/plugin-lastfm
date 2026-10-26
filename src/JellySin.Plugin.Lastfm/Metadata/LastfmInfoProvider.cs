using System.Net;
using System.Text.Json;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Transport;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace JellySin.Plugin.Lastfm.Metadata;

public abstract class LastfmInfoProvider<TItem, TLookup>(
    ILastfmClient client,
    ApplicationCredentialService credentials) : IRemoteMetadataProvider<TItem, TLookup>
    where TItem : BaseItem, IHasLookupInfo<TLookup>, new()
    where TLookup : ItemLookupInfo, new()
{
    public string Name => "Last.fm";

    protected abstract string Method { get; }

    public async Task<MetadataResult<TItem>> GetMetadata(TLookup info, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var app = await credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!app.IsConfigured || Plugin.Instance?.Configuration.Enabled == false)
            return new MetadataResult<TItem>();

        var (parameters, queriedById) = BuildParameters(info);
        if (parameters is null)
            return new MetadataResult<TItem>();

        try
        {
            using var document = await client.CallAsync(Method, parameters, null, RequestPriority.Background, cancellationToken).ConfigureAwait(false);
            return Parse(document, info, queriedById);
        }
        catch (LastfmException ex) when (ex.Code is 6)
        {
            return new MetadataResult<TItem>();
        }
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(TLookup searchInfo, CancellationToken cancellationToken)
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
        Describe(result, metadata.Item);
        return [result];
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    protected abstract (Dictionary<string, string>? Parameters, bool QueriedById) BuildParameters(TLookup info);

    protected abstract MetadataResult<TItem> Parse(JsonDocument document, TLookup info, bool queriedById);

    protected virtual void Describe(RemoteSearchResult result, TItem item)
    {
    }
}
