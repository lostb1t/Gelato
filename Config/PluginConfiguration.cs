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

    /// <summary>
    /// Where Gelato creates the folder it adds to a library picked on the settings page. Empty means
    /// the folder the movie path is in, so an install from before the library pickers keeps its folders
    /// next to each other.
    /// </summary>
    public string BasePath { get; set; } = "";
    public int StreamTTL { get; set; } = 3600;
    public int CatalogMaxItems { get; set; } = 100;
    public string Url { get; set; } = "";
    public bool EnableMixed { get; set; } = false;
    public bool ExtendLocalSeriesTrees { get; set; } = false;
    public bool FilterUnreleased { get; set; } = false;
    public int FilterUnreleasedBufferDays { get; set; } = 0;
    public bool DisableSourceCount { get; set; } = true;
    public string FFmpegAnalyzeDuration { get; set; } = "5M";
    public string FFmpegProbeSize { get; set; } = "40M";
    public bool CreateCollections { get; set; } = false;
    public int MaxCollectionItems { get; set; } = 100;
    public bool DisableSearch { get; set; } = false;
    /// <summary>
    /// The library-only switch in Jellyfin Web's search bar (Frontend/js/local-search-toggle.js).
    /// Named for when it was the only injected script.
    /// </summary>
    public bool EnableJavaScriptInjection { get; set; } = false;

    /// <summary>
    /// A title's page in Jellyfin Web shows the tracks of its version once the pre-probe has
    /// read them, without being opened again (Frontend/js/refresh-after-probe.js). Needs
    /// <see cref="PreProbe"/>.
    /// </summary>
    public bool RefreshVersionsAfterProbe { get; set; } = true;

    public bool LazyImages { get; set; } = false;

    /// <summary>
    /// A person's page also lists the person's films and series the library does not hold, from
    /// TMDB, as results that open like a search's. Not while <see cref="DisableSearch"/> keeps
    /// searches to the library: no remote requests then.
    /// </summary>
    public bool PersonRemoteTitles { get; set; } = true;

    /// <summary>
    /// A title put into the library because someone opened it from search stays out of the
    /// library's listings until someone plays it, marks it a favourite, likes it (a watchlist)
    /// or adds it to a collection or playlist, and is removed again when nobody did within
    /// <see cref="RemoveUnplayedSearchTitlesDays"/>. Search still finds it.
    /// </summary>
    public bool HideUnplayedSearchTitles { get; set; } = false;

    /// <summary>
    /// How many days a title opened from search stays hidden before it is removed. Not on the
    /// settings page: a fixed age that only the tests change.
    /// </summary>
    public double RemoveUnplayedSearchTitlesDays { get; set; } = DefaultRemoveUnplayedSearchTitlesDays;

    public const double DefaultRemoveUnplayedSearchTitlesDays = 5;

    /// <summary>
    /// How many hours the addon's manifest is used before it is read again
    /// (<see cref="Services.GelatoCache.ManifestLifetime"/>). Not on the settings page: a fixed
    /// lifetime that only the tests change.
    /// </summary>
    public double ManifestLifetimeHours { get; set; } = DefaultManifestLifetimeHours;

    public const double DefaultManifestLifetimeHours = 12;
    public List<CatalogConfig> Catalogs { get; set; } = [];
    public List<UserConfig> UserConfigs { get; set; } = [];

    /// <summary>
    /// Fill in a stream's tracks, runtime and size from RemuxDB when its streams are synced,
    /// so they show before playback and playback skips its probe.
    /// </summary>
    public bool RemuxDbEnabled { get; set; } = true;

    /// <summary>
    /// Probe a stream before it is played: when its item is opened or another version of it is
    /// picked, and the next episode while an episode nears its end. Playback of a stream
    /// that needs a probe then starts without waiting for it.
    /// </summary>
    public bool PreProbe { get; set; } = true;

    /// <summary>
    /// When the stream a title plays by default does not start (a dead or truncated debrid
    /// link), play the next of its streams instead. A version picked by hand is played as it is.
    /// </summary>
    public bool PlaybackFailover { get; set; }

    /// <summary>
    /// Submit a stream's probe to RemuxDB when it played a file RemuxDB did not know. Anonymous,
    /// and only for streams whose torrent is known.
    /// </summary>
    public bool RemuxDbContribute { get; set; } = true;

    public string RemuxDbUrl { get; set; } = RemuxDb.RemuxDbClient.DefaultUrl;

    /// <summary>
    /// Random id RemuxDB requires of every client, created on first use. Tied to nothing else.
    /// </summary>
    public string RemuxDbClientId { get; set; } = "";

    /// <summary>
    /// The Jellyfin version Gelato last started against, so it can tell when the server has been
    /// upgraded underneath it. Empty until the first start that records one.
    /// </summary>
    public string LastSeenServerVersion { get; set; } = "";

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

    public string GetBasePath() =>
        string.IsNullOrWhiteSpace(BasePath) ? GetDefaultBasePath() : BasePath.Trim();

    /// <summary>
    /// The base path when none is set: a "gelato" folder next to the movie path, or the folder the
    /// movie path is in when that is one already (the default install: %TEMP%/gelato). A movie path
    /// among real media, e.g. /media/gelato-movies, gets /media/gelato, not /media itself.
    /// </summary>
    public string GetDefaultBasePath()
    {
        var parent = string.IsNullOrWhiteSpace(MoviePath)
            ? null
            : Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(MoviePath.Trim()));
        if (string.IsNullOrWhiteSpace(parent))
            return Path.Combine(Path.GetTempPath(), "gelato");

        return string.Equals(Path.GetFileName(parent), "gelato", StringComparison.OrdinalIgnoreCase)
            ? parent
            : Path.Combine(parent, "gelato");
    }

    public PluginConfiguration GetEffectiveConfig(Guid userId)
    {
        var userConfig = UserConfigs.FirstOrDefault(u => u.UserId == userId);
        return userConfig is null ? this : userConfig.ApplyOverrides(this);
    }

    /// <summary>
    /// Every folder Gelato seeds a stub file into: the base movie and series paths, each
    /// per-user override and each catalog's own folder. A path configured more than once is
    /// returned once.
    /// </summary>
    public IReadOnlyList<GelatoLibraryPath> GetLibraryPaths()
    {
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal
        );
        var paths = new List<GelatoLibraryPath>();

        void Add(string label, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            string key;
            try
            {
                key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            }
            catch (Exception)
            {
                key = path;
            }

            if (seen.Add(key))
                paths.Add(new GelatoLibraryPath(label, path));
        }

        Add("Movies", MoviePath);
        Add("Series", SeriesPath);
        foreach (var user in UserConfigs)
        {
            Add("Movies (user override)", user.MoviePath);
            Add("Series (user override)", user.SeriesPath);
        }
        foreach (var catalog in Catalogs)
        {
            Add($"Catalog {catalog.Name}", catalog.Path);
        }

        return paths;
    }
}

/// <summary>A folder Gelato uses as a library location.</summary>
public sealed record GelatoLibraryPath(string Label, string Path);

public class UserConfig
{
    public Guid UserId { get; set; }
    public string Url { get; set; } = "";
    public string MoviePath { get; set; } = "";
    public string SeriesPath { get; set; } = "";
    public bool DisableSearch { get; set; } = false;

    /// <summary>
    /// The user's own <see cref="PluginConfiguration.PersonRemoteTitles"/>. On for an override saved
    /// before it existed, as the global default.
    /// </summary>
    public bool PersonRemoteTitles { get; set; } = true;

    /// <summary>
    /// Apply user overrides to base configuration - replaces all overridable fields
    /// </summary>
    public PluginConfiguration ApplyOverrides(PluginConfiguration baseConfig)
    {
        return new PluginConfiguration
        {
            // User overridable fields, no fallback to baseConfig, except that an empty URL means
            // the addon everyone uses
            Url = string.IsNullOrWhiteSpace(Url) ? baseConfig.Url : Url,
            MoviePath = MoviePath,
            SeriesPath = SeriesPath,
            DisableSearch = DisableSearch,
            PersonRemoteTitles = PersonRemoteTitles,

            // All other fields from base config
            StreamTTL = baseConfig.StreamTTL,
            CatalogMaxItems = baseConfig.CatalogMaxItems,
            EnableMixed = baseConfig.EnableMixed,
            ExtendLocalSeriesTrees = baseConfig.ExtendLocalSeriesTrees,
            FilterUnreleased = baseConfig.FilterUnreleased,
            FilterUnreleasedBufferDays = baseConfig.FilterUnreleasedBufferDays,
            HideUnplayedSearchTitles = baseConfig.HideUnplayedSearchTitles,
            RemoveUnplayedSearchTitlesDays = baseConfig.RemoveUnplayedSearchTitlesDays,
            DisableSourceCount = baseConfig.DisableSourceCount,
            FFmpegAnalyzeDuration = baseConfig.FFmpegAnalyzeDuration,
            FFmpegProbeSize = baseConfig.FFmpegProbeSize,
            CreateCollections = baseConfig.CreateCollections,
            MaxCollectionItems = baseConfig.MaxCollectionItems,
            RemuxDbEnabled = baseConfig.RemuxDbEnabled,
            PreProbe = baseConfig.PreProbe,
            PlaybackFailover = baseConfig.PlaybackFailover,
            RemuxDbContribute = baseConfig.RemuxDbContribute,
            RemuxDbUrl = baseConfig.RemuxDbUrl,
            RemuxDbClientId = baseConfig.RemuxDbClientId,
            UserConfigs = baseConfig.UserConfigs,
        };
    }
}

public class GelatoStremioProviderFactory(
    IHttpClientFactory http,
    ILoggerFactory log,
    Services.GelatoCache cache
)
{
    public GelatoStremioProvider Create(Guid userId)
    {
        var cfg = GelatoPlugin.Instance!.Configuration.GetEffectiveConfig(userId);
        return Create(cfg);
    }

    public GelatoStremioProvider Create(PluginConfiguration cfg)
    {
        return cache.Provider(
            cfg.GetBaseUrl(),
            url => new GelatoStremioProvider(url, http, log.CreateLogger<GelatoStremioProvider>())
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

    /// <summary>
    /// The folder this catalog's items go into, instead of the movie or series folder. Empty means
    /// the default folders. Added to a Jellyfin library, it gives the catalog a library of its own.
    /// </summary>
    public string Path { get; set; } = "";

    public int GetMaxItems(int globalMaxItems) => MaxItems > 0 ? MaxItems : globalMaxItems;

    /// <summary>What the catalog's items are marked with: an id alone is shared by its types.</summary>
    public string GetKey() => $"{Type}:{Id}";
}
