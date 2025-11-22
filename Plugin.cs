using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Gelato.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Gelato;

public class GelatoPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<GelatoPlugin> _log;
    private readonly ILibraryManager _library;
    private readonly GelatoManager _manager;
    public ConcurrentDictionary<Guid, PluginConfiguration> UserConfigs { get; } = new();
    private readonly IHttpClientFactory _http;
    private readonly GelatoStremioProviderFactory _stremioFactory;

    public GelatoPlugin(
        IApplicationPaths applicationPaths,
        GelatoManager manager,
        IXmlSerializer xmlSerializer,
        ILogger<GelatoPlugin> log,
        IHttpClientFactory http,
        GelatoStremioProviderFactory stremioFactory,
        ILibraryManager library
    )
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _log = log;
        _library = library;
        _manager = manager;
        _http = http;
        _stremioFactory = stremioFactory;

        //  _manager.TryGetMovieFolder();
        //  _manager.TryGetSeriesFolder();
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
        var cfg = Instance.Configuration;

        if (userId == Guid.Empty)
        {
            var stremio = _stremioFactory.Create(cfg);
            cfg.stremio = stremio;
            cfg.MovieFolder = _manager.TryGetMovieFolder(cfg);
            cfg.SeriesFolder = _manager.TryGetSeriesFolder(cfg);
            return cfg;
        }

        return UserConfigs.GetOrAdd(
            userId,
            _ =>
            {
                var userConfig = Instance.Configuration.UserConfigs.FirstOrDefault(u =>
                    u.UserId == userId
                );
                var cfg =
                    userConfig?.ApplyOverrides(Instance.Configuration) ?? Instance.Configuration;
                var stremio = _stremioFactory.Create(cfg);
                cfg.stremio = stremio;
                cfg.MovieFolder = _manager.TryGetMovieFolder(cfg);
                cfg.SeriesFolder = _manager.TryGetSeriesFolder(cfg);
                return cfg;
            }
        );
    }
}
