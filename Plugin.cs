using Gelato.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;


namespace Gelato;

public class GelatoPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<GelatoPlugin> _log;
    private readonly ILibraryManager _library;
    private readonly GelatoManager _manager;
    public GelatoPlugin(IApplicationPaths applicationPaths, GelatoManager manager, IXmlSerializer xmlSerializer, ILogger<GelatoPlugin> log, ILibraryManager library)
    : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _log = log;
        _library = library;
        _manager = manager;
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
        base.UpdateConfiguration(configuration);
        var cfg = (PluginConfiguration)configuration;

        if (cfg.MoviePath is not null)
        {
            GelatoManager.SeedFolder(cfg.MoviePath);
        }
        if (cfg.SeriesPath is not null)
        {
            GelatoManager.SeedFolder(cfg.SeriesPath);
        }
    }
}

