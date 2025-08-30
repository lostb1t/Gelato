using Jellyfin.Database.Implementations.Entities.Libraries;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using System;

namespace Jellyfin.Plugin.ExternalMedia.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {

        public Guid? MovieLibraryId { get; set; }
        public Guid? MovieFolderId { get; set; }

        public string Url { get; set; } = "";

        public string GetBaseUrl()
        {
            if (string.IsNullOrWhiteSpace(Url))
                throw new InvalidOperationException("ExternalMedia Url not configured.");

            var u = Url.Trim().TrimEnd('/');

            if (u.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
                u = u[..^"/manifest.json".Length];

            return u;
        }
    }
}