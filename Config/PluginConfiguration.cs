using System.Text.Json.Serialization;
using System.Xml.Serialization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging;

namespace Gelato.Config;

public class PluginConfiguration : BasePluginConfiguration
{
    public string MoviePath { get; set; } = Path.Combine(Path.GetTempPath(), "gelato", "movies");
    public string SeriesPath { get; set; } = Path.Combine(Path.GetTempPath(), "gelato", "series");
    public string AnimeMoviePath { get; set; } =
        Path.Combine(Path.GetTempPath(), "gelato", "anime-movies");
    public string AnimeSeriesPath { get; set; } =
        Path.Combine(Path.GetTempPath(), "gelato", "anime-series");
    public int StreamTTL { get; set; } = 3600;
    public int CatalogMaxItems { get; set; } = 100;
    public string Url { get; set; } = "";
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

    public List<CatalogConfig> Catalogs { get; set; } = [];
    public List<UserConfig> UserConfigs { get; set; } = [];

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
    public GelatoStremioProvider? Stremio;

    [JsonIgnore]
    [XmlIgnore]
    public Folder? MovieFolder;

    [JsonIgnore]
    [XmlIgnore]
    public Folder? SeriesFolder;

    [JsonIgnore]
    [XmlIgnore]
    public Folder? AnimeMovieFolder;

    [JsonIgnore]
    [XmlIgnore]
    public Folder? AnimeSeriesFolder;

    public PluginConfiguration GetEffectiveConfig(Guid userId)
    {
        var userConfig = UserConfigs.FirstOrDefault(u => u.UserId == userId);
        return userConfig is null ? this : userConfig.ApplyOverrides(this);
    }
}

public class UserConfig
{
    public Guid UserId { get; set; }
    public string Url { get; set; } = "";
    public string MoviePath { get; set; } = "";
    public string SeriesPath { get; set; } = "";
    public string AnimeMoviePath { get; set; } = "";
    public string AnimeSeriesPath { get; set; } = "";
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
            AnimeMoviePath = AnimeMoviePath,
            AnimeSeriesPath = AnimeSeriesPath,
            DisableSearch = DisableSearch,

            // All other fields from base config
            StreamTTL = baseConfig.StreamTTL,
            CatalogMaxItems = baseConfig.CatalogMaxItems,
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

public class GelatoStremioProviderFactory(IHttpClientFactory http, ILoggerFactory log)
{
    public GelatoStremioProvider Create(Guid userId)
    {
        var cfg = GelatoPlugin.Instance!.Configuration.GetEffectiveConfig(userId);
        return new GelatoStremioProvider(
            cfg.GetBaseUrl(),
            http,
            log.CreateLogger<GelatoStremioProvider>()
        );
    }

    public GelatoStremioProvider Create(PluginConfiguration cfg)
    {
        return new GelatoStremioProvider(
            cfg.GetBaseUrl(),
            http,
            log.CreateLogger<GelatoStremioProvider>()
        );
    }
}

public class CatalogConfig
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "movie";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = false;

    /// <summary>0 means "use global CatalogMaxItems".</summary>
    public int MaxItems { get; set; } = 0;
    public bool CreateCollection { get; set; } = false;
    public string Url { get; set; } = "";
}
