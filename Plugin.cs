using System.Collections.Concurrent;
using Gelato.Config;
using Gelato.ScheduledTasks;
using Gelato.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato;

public class GelatoPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<GelatoPlugin> _log;
    private readonly GelatoManager _manager;
    private ConcurrentDictionary<Guid, PluginConfiguration> UserConfigs { get; } = new();
    private readonly GelatoStremioProviderFactory _stremioFactory;
    private readonly ITaskManager _taskManager;
    private readonly GelatoCache _cache;
    public PalcoCacheService PalcoCache { get; } // Migrated Palco Cache Service

    public GelatoPlugin(
        IApplicationPaths applicationPaths,
        GelatoManager manager,
        IXmlSerializer xmlSerializer,
        ILogger<GelatoPlugin> log,
        GelatoStremioProviderFactory stremioFactory,
        PalcoCacheService palcoCache,
        ITaskManager taskManager,
        GelatoCache cache
    )
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _log = log;
        _manager = manager;
        _stremioFactory = stremioFactory;
        PalcoCache = palcoCache;
        _taskManager = taskManager;
        _cache = cache;
    }

    public static GelatoPlugin? Instance { get; private set; }

    /// <summary>
    /// The User-Agent Gelato sends to addons and RemuxDB: <c>Gelato/&lt;version&gt;</c>.
    /// </summary>
    public static string UserAgent { get; } =
        $"Gelato/{typeof(GelatoPlugin).Assembly.GetName().Version?.ToString() ?? "0"}";

    // Event fired when the plugin configuration is updated via UpdateConfiguration
    public static new event Action<PluginConfiguration>? ConfigurationChanged;

    public override string Name => "Gelato";
    public override Guid Id => Guid.Parse("94EA4E14-8163-4989-96FE-0A2094BC2D6A");
    public override string Description =>
        "Gelato brings AIOStreams (Stremio addons) into Jellyfin. Search results and imported catalogs show up as regular movies and series, and streams are resolved when you press play and proxied through Jellyfin.";

    private static readonly string[] PageFiles =
    [
        "gelato.css",
        "gelato-common.js",
        "gelato-settings.js",
        "gelato-setup.js",
    ];

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var prefix = GetType().Namespace;
        yield return new PluginPageInfo
        {
            Name = "config",
            EnableInMainMenu = true,
            MenuIcon = "movie",
            EmbeddedResourcePath = prefix + ".Config.config.html",
        };

        // The page's style and scripts, which config.html and its scripts load by these names.
        foreach (var file in PageFiles)
        {
            yield return new PluginPageInfo
            {
                Name = file,
                EmbeddedResourcePath = prefix + ".Config." + file,
            };
        }
    }

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        var cfg = (PluginConfiguration)configuration;
        var treesChanged = Configuration.ExtendLocalSeriesTrees != cfg.ExtendLocalSeriesTrees;
        base.UpdateConfiguration(cfg);

        // Only what came from the old configuration: what search results became stays, a client
        // keeps their ids in its page (GelatoManager.ClearCache, for the purges, drops that too).
        _cache.Invalidate();
        _stremioFactory.ClearCache();
        UserConfigs.Clear();

        // A changed movie or series path can be the folder items without one were waiting for.
        _manager.AdoptParkedSoon();

        // The task adds the missing seasons and episodes to local series, or removes them once
        // the setting is off. Jellyfin starts it now when idle; a run in progress read the old
        // value, so it runs once more after that one (its queue holds a task once).
        if (treesChanged)
        {
            _log.LogInformation(
                "Extending local series trees turned {State}, queueing the series tree sync",
                cfg.ExtendLocalSeriesTrees ? "on" : "off"
            );
            _taskManager.QueueScheduledTask<SyncSeriesTreesTask>();
        }

        // Notify subscribers that configuration changed
        try
        {
            ConfigurationChanged?.Invoke(cfg);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Error while invoking ConfigurationChanged event");
        }
    }

    public PluginConfiguration GetConfig(Guid userId)
    {
        try
        {
            var cfg = UserConfigs.GetOrAdd(
                userId,
                _ =>
                {
                    var built = Instance?.Configuration;
                    if (userId != Guid.Empty)
                    {
                        var userConfig = Instance?.Configuration.UserConfigs.FirstOrDefault(u =>
                            u.UserId == userId
                        );
                        built =
                            userConfig?.ApplyOverrides(Instance?.Configuration)
                            ?? Instance?.Configuration;
                    }
                    built.Stremio = _stremioFactory.Create(built);
                    return built;
                }
            );

            // Resolved on every call rather than once with the cached entry. The libraries
            // backing these paths are usually added after Gelato is first configured, and a
            // null cached from before they existed would never recover on its own — the
            // symptom being imports that quietly do nothing until the server is restarted.
            // GelatoManager memoizes the underlying lookup, so this stays cheap.
            cfg.MovieFolder = _manager.TryGetMovieFolder(cfg);
            cfg.SeriesFolder = _manager.TryGetSeriesFolder(cfg);
            // Seeds the catalogs' folders too, so one can be added to a library right away.
            _manager.GetCatalogFolders(cfg);
            return cfg;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Error getting config");
            return new PluginConfiguration();
        }
    }
}
