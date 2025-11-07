using System;
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
private readonly GelatoStremioProvider _provider;

    public GelatoPlugin(
        IApplicationPaths applicationPaths,
        GelatoManager manager,
GelatoStremioProvider provider,
        IXmlSerializer xmlSerializer,
        ILogger<GelatoPlugin> log,
        ILibraryManager library
    )
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _log = log;
        _library = library;
        _manager = manager;
        _provider = provider;

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
        _manager.TryGetMovieFolder();
        _manager.TryGetSeriesFolder();
await _provider.GetManifestAsync(true);
    }
}
