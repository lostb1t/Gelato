using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Xml.Serialization;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gelato.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string MoviePath { get; set; } =
            Path.Combine(Path.GetTempPath(), "gelato", "movies");
        public string SeriesPath { get; set; } =
            Path.Combine(Path.GetTempPath(), "gelato", "series");
        public bool UseStrm { get; set; } = false;
        public int StreamTTL { get; set; } = 3600;
        public int CatalogMaxItems { get; set; } = 100;
        public string Url { get; set; } = "";
        public bool EnableSubs { get; set; } = false;
        public bool EnableMixed { get; set; } = false;
        public bool FilterUnreleased { get; set; } = false;
        public int FilterUnreleasedBufferDays { get; set; } = 30;
        public bool DisableSourceCount { get; set; } = true;
        public bool P2PEnabled { get; set; } = false;
        public int P2PDLSpeed { get; set; } = 0;
        public int P2PULSpeed { get; set; } = 0;
        public string FFmpegAnalyzeDuration { get; set; } = "5M";
        public string FFmpegProbeSize { get; set; } = "40M";
        public bool CreateCollections { get; set; } = false;
        public int MaxCollectionItems { get; set; } = 100;
        public bool DisableSearch { get; set; } = false;

        public List<UserConfig> UserConfigs { get; set; } = new List<UserConfig>();

        public string GetBaseUrl()
        {
            if (string.IsNullOrWhiteSpace(Url))
                throw new InvalidOperationException("Gelato Url not configured.");

            var u = Url.Trim().TrimEnd('/');

            if (u.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
                u = u[..^"/manifest.json".Length];

            return u;
        }

        [JsonIgnore]
        [XmlIgnore]
        public GelatoStremioProvider? stremio;

        [JsonIgnore]
        [XmlIgnore]
        public Folder? MovieFolder;

        [JsonIgnore]
        [XmlIgnore]
        public Folder? SeriesFolder;

        public PluginConfiguration GetEffectiveConfig(Guid userId)
        {
            var userConfig = UserConfigs.FirstOrDefault(u => u.UserId == userId);
            if (userConfig is null)
            {
                return this;
            }
            return userConfig.ApplyOverrides(this);
        }
    }

    public class UserConfig
    {
        public Guid UserId { get; set; }
        public string Url { get; set; } = "";
        public string MoviePath { get; set; } = "";
        public string SeriesPath { get; set; } = "";
        public bool DisableSearch { get; set; } = false;

        /// <summary>
        /// Apply user overrides to base configuration - replaces all overridable fields
        /// </summary>
        public PluginConfiguration ApplyOverrides(PluginConfiguration baseConfig)
        {
            return new PluginConfiguration
            {
                // User overridable fields - all required, no fallback to baseConfig
                Url = Url,
                MoviePath = MoviePath,
                SeriesPath = SeriesPath,
                DisableSearch = DisableSearch,

                // All other fields from base config
                StreamTTL = baseConfig.StreamTTL,
                CatalogMaxItems = baseConfig.CatalogMaxItems,
                EnableSubs = baseConfig.EnableSubs,
                EnableMixed = baseConfig.EnableMixed,
                FilterUnreleased = baseConfig.FilterUnreleased,
                FilterUnreleasedBufferDays = baseConfig.FilterUnreleasedBufferDays,
                DisableSourceCount = baseConfig.DisableSourceCount,
                P2PEnabled = baseConfig.P2PEnabled,
                P2PDLSpeed = baseConfig.P2PDLSpeed,
                P2PULSpeed = baseConfig.P2PULSpeed,
                FFmpegAnalyzeDuration = baseConfig.FFmpegAnalyzeDuration,
                FFmpegProbeSize = baseConfig.FFmpegProbeSize,
                CreateCollections = baseConfig.CreateCollections,
                MaxCollectionItems = baseConfig.MaxCollectionItems,
                UserConfigs = baseConfig.UserConfigs,
            };
        }
    }

    public class GelatoStremioProviderFactory
    {
        private readonly ILibraryManager _library;
        private readonly IHttpClientFactory _http;
        private readonly ILoggerFactory _log;

        public GelatoStremioProviderFactory(
            ILibraryManager library,
            IHttpClientFactory http,
            ILoggerFactory log
        )
        {
            _library = library;
            _http = http;
            _log = log;
        }

        public GelatoStremioProvider Create(Guid userId)
        {
            var cfg = GelatoPlugin.Instance!.Configuration.GetEffectiveConfig(userId);
            return new GelatoStremioProvider(
                cfg.GetBaseUrl(),
                _http,
                _log.CreateLogger<GelatoStremioProvider>()
            );
        }

        public GelatoStremioProvider Create(PluginConfiguration cfg)
        {
            return new GelatoStremioProvider(
                cfg.GetBaseUrl(),
                _http,
                _log.CreateLogger<GelatoStremioProvider>()
            );
        }
    }
}
