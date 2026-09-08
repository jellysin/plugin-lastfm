using JellySin.Plugin.Lastfm.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace JellySin.Plugin.Lastfm;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private volatile bool _listeningEnabled;
    public static readonly Guid PluginId = new("2034650d-a290-4a16-b195-89fb44cfb932");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer) : base(applicationPaths, xmlSerializer)
    {
        _listeningEnabled = Configuration.Enabled;
        Instance = this;
    }

    public bool ListeningEnabled => _listeningEnabled;

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        base.UpdateConfiguration(configuration);
        _listeningEnabled = Configuration.Enabled;
    }

    public static Plugin? Instance { get; private set; }

    public override Guid Id => PluginId;

    public override string Name => "JellySin Last.fm";

    public override string Description => "Connect your music: Last.fm listening, favourites, discovery and playlists for Jellyfin 12.";

    public IEnumerable<PluginPageInfo> GetPages() => [new PluginPageInfo
    {
        Name = "jellysin-lastfm",
        EmbeddedResourcePath = "JellySin.Plugin.Lastfm.Web.admin.html"
    }];
}
