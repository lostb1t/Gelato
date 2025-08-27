using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ExternalMedia;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool DisableRemoteImagesForExternalItems { get; set; } = true;
    public string ExternalHlsTemplate { get; set; } = "https://cdn.example.com/vod/{id}/master.m3u8";
}
