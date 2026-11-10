using System.Net;
using System.Text.Json;
using JellySin.Plugin.Lastfm.Configuration;
using JellySin.Plugin.Lastfm.Features;
using JellySin.Plugin.Lastfm.Transport;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace JellySin.Plugin.Lastfm.Metadata;

/// <summary>Provides shared Last.fm metadata lookup and parsing.</summary>
public abstract class LastfmInfoProvider<TItem, TLookup>(
    ILastfmClient client,
    ApplicationCredentialService credentials) : IRemoteMetadataProvider<TItem, TLookup>
    where TItem : BaseItem, IHasLookupInfo<TLookup>, new()
    where TLookup : ItemLookupInfo, new()
{
    /// <summary>Gets the metadata provider name.</summary>
    public string Name => "Last.fm";

    /// <summary>Gets the Last.fm lookup method.</summary>
    protected abstract string Method { get; }

    /// <summary>Gets the MusicBrainz provider identifier key.</summary>
    protected abstract string MusicBrainzKey { get; }

    /// <summary>Gets the root child node containing the item.</summary>
    protected abstract string ItemNode { get; }

    /// <summary>Gets the child node containing the wiki text.</summary>
    protected abstract string TextNode { get; }

    /// <summary>Retrieves metadata for the supplied lookup information.</summary>
    public async Task<MetadataResult<TItem>> GetMetadata(TLookup info, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var app = await credentials.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!app.IsConfigured || Plugin.Instance?.Configuration.Enabled == false)
        {
            return new MetadataResult<TItem>();
        }

        var (parameters, queriedById) = BuildParameters(info);
        if (parameters is null)
        {
            return new MetadataResult<TItem>();
        }

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

    /// <summary>Retrieves search results from the matching metadata.</summary>
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(TLookup searchInfo, CancellationToken cancellationToken)
    {
        var metadata = await GetMetadata(searchInfo, cancellationToken).ConfigureAwait(false);
        if (!metadata.HasMetadata || metadata.Item is null)
        {
            return [];
        }

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

    /// <summary>Returns a not-found response because images are not provided.</summary>
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>Builds a name query or returns null when required names are missing.</summary>
    protected abstract Dictionary<string, string>? NameQuery(TLookup info);

    /// <summary>Fills additional item fields from the response node.</summary>
    protected virtual void Fill(TItem item, JsonElement node)
    {
    }

    /// <summary>Builds an ordinal query with automatic correction disabled.</summary>
    protected static Dictionary<string, string> Query(params (string Key, string Value)[] pairs)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            query.Add(pair.Key, pair.Value);
        }

        query.Add("autocorrect", "0");
        return query;
    }

    /// <summary>Prefers a MusicBrainz identifier over a name query.</summary>
    private (Dictionary<string, string>? Parameters, bool QueriedById) BuildParameters(TLookup info)
    {
        if (info.ProviderIds is not null && info.ProviderIds.TryGetValue(MusicBrainzKey, out var mbid) && !string.IsNullOrWhiteSpace(mbid))
        {
            return (Query(("mbid", mbid.Trim())), true);
        }

        return (NameQuery(info), false);
    }

    /// <summary>Parses shared item metadata and fills provider-specific fields.</summary>
    private MetadataResult<TItem> Parse(JsonDocument document, TLookup info, bool queriedById)
    {
        var node = MusicJson.Child(document.RootElement, ItemNode);
        if (node.ValueKind != JsonValueKind.Object)
        {
            return new MetadataResult<TItem>();
        }

        var textNode = MusicJson.Child(node, TextNode);
        var overview = MusicJson.CleanWiki(textNode);
        if (string.IsNullOrWhiteSpace(overview))
        {
            return new MetadataResult<TItem> { HasMetadata = false };
        }

        var name = MusicJson.Text(node, "name");
        var item = new TItem
        {
            Name = string.IsNullOrWhiteSpace(name) ? info.Name : name,
            Overview = overview
        };
        Fill(item, node);

        return new MetadataResult<TItem>
        {
            HasMetadata = true,
            Item = item,
            QueriedById = queriedById
        };
    }

    /// <summary>Copies additional item fields into a search result.</summary>
    protected virtual void Describe(RemoteSearchResult result, TItem item)
    {
    }
}
