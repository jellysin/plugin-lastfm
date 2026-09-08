using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace JellySin.NativeProbe;

/// <summary>Disposable-host integration fixture. Never include this assembly in a release.</summary>
public sealed class Plugin(IApplicationPaths paths, IXmlSerializer serializer) : BasePlugin<BasePluginConfiguration>(paths, serializer)
{
    public override Guid Id => new("ad72a1cf-3b21-4d97-846c-1fbb1412f6d0");

    public override string Name => "JellySin Native Probe (test only)";

    public override string Description => "Admin-only userdata integration fixture for isolated generated test hosts.";
}
