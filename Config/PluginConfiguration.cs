using System;
using System.Reflection;
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

        /// <summary>
        /// Get effective configuration for a specific user
        /// </summary>
        public PluginConfiguration GetEffectiveConfig(Guid? userId)
        {
            if (userId is null)
            {
                return this;
            }
            var userConfig = UserConfigs.FirstOrDefault(u => u.UserId == userId);

            return userConfig.ApplyOverrides(this);
        }
    }

    public class UserConfig
    {
        public Guid UserId { get; set; }
        public string? Url { get; set; }
        public string? MoviePath { get; set; }
        public string? SeriesPath { get; set; }
        public bool? DisableSearch { get; set; }

        /// <summary>
        /// Apply user overrides to base configuration
        /// </summary>
        public PluginConfiguration ApplyOverrides(PluginConfiguration baseConfig)
        {
            return new PluginConfiguration
            {
                // User overridable fields
                Url = Url ?? baseConfig.Url,
                MoviePath = MoviePath ?? baseConfig.MoviePath,
                SeriesPath = SeriesPath ?? baseConfig.SeriesPath,
                DisableSearch = DisableSearch ?? baseConfig.DisableSearch,

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
        private readonly IItemRepository _repo;

        public GelatoStremioProviderFactory(
            ILibraryManager library,
            IHttpClientFactory http,
            ILoggerFactory log,
            IItemRepository repo
        )
        {
            _library = library;
            _http = http;
            _log = log;
            _repo = repo;
        }

        public GelatoStremioProvider Create(Guid? userId)
        {
            var cfg = GelatoPlugin.Instance!.Configuration.GetEffectiveConfig(userId);
            return new GelatoStremioProvider(
                cfg.GetBaseUrl(),
                _library,
                _http,
                _log.CreateLogger<GelatoStremioProvider>(),
                _repo
            );
        }
    }
}
