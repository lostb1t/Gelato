using System.Collections.Concurrent;
using Gelato.Config;
using Gelato.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Gelato;

public class GelatoPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<GelatoPlugin> _log;
    private readonly GelatoManager _manager;
    private ConcurrentDictionary<Guid, PluginConfiguration> UserConfigs { get; } = new();
    private readonly GelatoStremioProviderFactory _stremioFactory;
    public PalcoCacheService PalcoCache { get; } // Migrated Palco Cache Service

    public GelatoPlugin(
        IApplicationPaths applicationPaths,
        GelatoManager manager,
        IXmlSerializer xmlSerializer,
        ILogger<GelatoPlugin> log,
        GelatoStremioProviderFactory stremioFactory,
        PalcoCacheService palcoCache
    )
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _log = log;
        _manager = manager;
        _stremioFactory = stremioFactory;
        PalcoCache = palcoCache;
    }

    public static GelatoPlugin? Instance { get; private set; }

    // Event fired when the plugin configuration is updated via UpdateConfiguration
    public static new event Action<PluginConfiguration>? ConfigurationChanged;

    public override string Name => "Chocolate Gelato";
    public override Guid Id => Guid.Parse("E2513B6C-E574-47A5-B89D-CE05BF975685");
    public override string Description => "on-demand MediaSources and optional image suppression.";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var prefix = GetType().Namespace;
        yield return new PluginPageInfo
        {
            Name = "config",
            EnableInMainMenu = true,
            EmbeddedResourcePath = prefix + ".Config.config.html",
        };
    }

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        var cfg = (PluginConfiguration)configuration;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISABLE_P2P")))
        {
            cfg.P2PEnabled = false;
        }
        base.UpdateConfiguration(cfg);

        _manager.ClearCache();
        _stremioFactory.ClearCache();
        UserConfigs.Clear();

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
            return cfg;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Error getting config");
            return new PluginConfiguration();
        }
    }
}
