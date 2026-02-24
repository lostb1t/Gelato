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

    public override string Name => "Gelato";
    public override Guid Id => Guid.Parse("94EA4E14-8163-4989-96FE-0A2094BC2D6A");
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
        UserConfigs.Clear();
    }

    public PluginConfiguration GetConfig(Guid userId)
    {
        try
        {
            return UserConfigs.GetOrAdd(
                userId,
                _ =>
                {
                    var cfg = Instance?.Configuration;
                    if (userId != Guid.Empty)
                    {
                        var userConfig = Instance?.Configuration.UserConfigs.FirstOrDefault(u =>
                            u.UserId == userId
                        );
                        cfg =
                            userConfig?.ApplyOverrides(Instance?.Configuration)
                            ?? Instance?.Configuration;
                    }
                    var stremio = _stremioFactory.Create(cfg);
                    cfg.Stremio = stremio;
                    cfg.MovieFolder = _manager.TryGetMovieFolder(cfg);
                    cfg.SeriesFolder = _manager.TryGetSeriesFolder(cfg);
                    return cfg;
                }
            );
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Error getting config");
            return new PluginConfiguration();
        }
    }
}
