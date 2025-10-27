using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;

namespace Gelato.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {

        public string MoviePath { get; set; } = Path.Combine(Path.GetTempPath(), "gelato", "movies");
        public string SeriesPath { get; set; } = Path.Combine(Path.GetTempPath(), "gelato", "series");
        public int StreamTTL { get; set; } = 3600;
        public int CatalogMaxItems { get; set; } = 100;
        public string Url { get; set; } = "";
        public bool EnableSubs { get; set; } = false;
        public bool EnableMixed { get; set; } = true;
        public bool FilterUnreleased { get; set; } = false;
        public int FilterUnreleasedBufferDays { get; set; } = 30;
        public bool P2PEnabled { get; set; } = false;
        public int P2PDLSpeed { get; set; } = 0;
        public int P2PULSpeed { get; set; } = 0;
        public string FFmpegAnalyzeDuration { get; set; } = "5M";
        public string FFmpegProbeSize { get; set; } = "25M";

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
