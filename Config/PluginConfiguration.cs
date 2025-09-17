using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using System;

namespace Gelato.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {

        public string? MoviePath { get; set; }
        public string? SeriesPath { get; set; }
        public int CatalogMaxItems { get; set; } = 250;
        public string Url { get; set; } = "";
        public bool EnableSubs { get; set; } = false;


        public string GetBaseUrl()
        {
            if (string.IsNullOrWhiteSpace(Url))
                throw new InvalidOperationException("Gelato Url not configured.");

            var u = Url.Trim().TrimEnd('/');

            if (u.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
                u = u[..^"/manifest.json".Length];

            return u;
        }
    }
}