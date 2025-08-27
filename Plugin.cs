using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ExternalMedia;

public class ExternalMediaPlugin : BasePlugin<PluginConfiguration>
{

    public ExternalMediaPlugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
    : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static ExternalMediaPlugin? Instance { get; private set; }

    public override string Name => "External Media";
    public override Guid Id => Guid.Parse("94EA4E14-8163-4989-96FE-0A2094BC2D6A");
    public override string Description => "Adds virtual items (external://…) with on-demand MediaSources and optional image suppression.";
}


