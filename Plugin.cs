using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Plugins;
using Jellyfin.Plugin.ExternalMedia.Configuration;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Library;
using Jellyfin.Plugin.ExternalMedia.Common;
using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace Jellyfin.Plugin.ExternalMedia;


public class ExternalMediaPlugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly ILogger<ExternalMediaPlugin> _log;
    private readonly ILibraryManager _library;
    private readonly ExternalMediaManager _manager;
    public ExternalMediaPlugin(IApplicationPaths applicationPaths, ExternalMediaManager manager, IXmlSerializer xmlSerializer, ILogger<ExternalMediaPlugin> log, ILibraryManager library)
    : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _log = log;
        _library = library;
        _manager = manager;
    }

    public static ExternalMediaPlugin? Instance { get; private set; }

    public override string Name => "External Media";
    public override Guid Id => Guid.Parse("94EA4E14-8163-4989-96FE-0A2094BC2D6A");
    public override string Description => "Adds virtual items (external://…) with on-demand MediaSources and optional image suppression.";

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
        _log.LogInformation("ExternalMedia: UpdateConfiguration");
        _log.LogInformation("ExternalMedia: Movie library {Id}", cfg.MovieLibraryId);
        // var cfg = (PluginConfiguration)configuration;

        //var library = Helpers.TryGetMovieLibrary(cfg, _library);
        //if (library is null)
        //{
        //    _log.LogWarning("ExternalMedia: No MovieLibrary configured");
        //    return;
        //}

        //_manager.EnsureLib(library);

        //var folder = _manager.TryGetMovieFolder(cfg);
        //cfg.MovieFolderId = folder?.Id;
       // _log.LogInformation("ExternalMedia: Movie Folder {Id}", cfg.MovieFolderId);
       // base.UpdateConfiguration(cfg);
       // _log.LogWarning("ExternalMedia: Movie Folder {Id}", folder.Id);

        // // Do your custom logic here
        // if (cfg.MovieLibraryId is Guid libId)
        // {
        //     var lib = _libraryManager.GetItemById(libId) as Folder;
        //     if (lib != null)
        //     {
        //         // Check if the "Stremio" folder already exists
        //         var existing = lib.GetChildren(new InternalSystemUser(), true)
        //                           .FirstOrDefault(x => x.Name == "Stremio");
        //         if (existing == null)
        //         {
        //             var stremioFolder = new Folder
        //             {
        //                 Name = "Stremio",
        //                 Id = _libraryManager.GetNewItemId("stremio", typeof(Folder)),
        //                 Path = "stremio://", // or some fake path to distinguish
        //             };
        //             lib.AddChild(stremioFolder);
        //             _libraryManager.UpdateItem(stremioFolder, lib, CancellationToken.None);
        //         }
        //     }
        // }
    }
}
