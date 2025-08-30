#pragma warning disable SA1611
#pragma warning disable SA1591
#pragma warning disable SA1615
#pragma warning disable CS0165

using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities;
using Jellyfin.Plugin.ExternalMedia.Configuration;
using MediaBrowser.Model.Configuration;
using System.Text;
using Jellyfin.Data.Enums;

using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Providers;          // MetadataRefreshOptions, MetadataRefreshMode, DirectoryService
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.ExternalMedia;

public class ExternalMediaManager
{
    private readonly ILogger<ExternalMediaManager> _log;
    private readonly ILoggerFactory _loggerFactory;
    // private readonly IFileSystem _fileSystem;
    private readonly ExternalMediaStremioProvider _provider;
    private readonly IServerConfigurationManager _config;
    private readonly IUserManager _user;
    private readonly ILibraryManager _library;
    private readonly IItemRepository _repo;
    private readonly IDtoService _dtoService;
    private readonly IFileSystem _fileSystem;
    // private readonly string path = "/media/external/movies";
    // private readonly string name = "external_movies";

    public ExternalMediaManager(
        ILoggerFactory loggerFactory,
        ExternalMediaStremioProvider provider,
        IDtoService dtoService,
        IServerConfigurationManager config,
        IUserManager userManager,
IItemRepository repo,
          IFileSystem fileSystem,
        ILibraryManager libraryManager)
    {
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<ExternalMediaManager>();
        _provider = provider; _dtoService = dtoService;
        _config = config;
        _repo = repo;
        _user = userManager;
        _library = libraryManager;
        _fileSystem = fileSystem;
    }

    // private static void SeedFolder(string path)
    // {
    //     Directory.CreateDirectory(path);
    //     var seed = System.IO.Path.Combine(path, "stub.txt");
    //     if (!File.Exists(seed))
    //         File.WriteAllBytes(seed, Array.Empty<byte>());
    // }

    // public Folder? TryGetMovieLibrary(PluginConfiguration cfg)
    // {
    //     if (cfg.MovieLibraryId is Guid id)
    //         return _library.GetItemById(id) as Folder;
    //     return null;
    // }

    // public static Folder? TryGetMovieFolder(this PluginConfiguration cfg, ILibraryManager library)
    // {
    //     // var lib = TryGetMovieLibrary(cfg, library);
    //     // if (cfg.MovieFolderId is Guid id)
    //     // {
    //     //     return library.GetItemList(new InternalItemsQuery
    //     //     {
    //     //         ParentId = id
    //     //     })
    //     //         .OfType<Folder>()
    //     //         .FirstOrDefault();
    //     // }
    //     if (cfg.MovieFolderId is Guid id)
    //         return library.GetItemById(id) as Folder;
    //     return null;
    // }

    public Folder? TryGetMovieFolder()
    {
        var cfg = ExternalMediaPlugin.Instance!.Configuration;
        // var lib = TryGetMovieLibrary(cfg, library);

        return _library.GetItemList(new InternalItemsQuery
        {
            // ParentId = id
            Path = cfg.MoviePath
        })
            .OfType<Folder>()
            .FirstOrDefault();
    }

    public Folder? TryGetSeriesFolder()
    {
        var cfg = ExternalMediaPlugin.Instance!.Configuration;
        // var lib = TryGetMovieLibrary(cfg, library);

        return _library.GetItemList(new InternalItemsQuery
        {
            // ParentId = id
            Path = cfg.SeriesPath
        })
            .OfType<Folder>()
            .FirstOrDefault();
    }


    //     public void CreateStremioFolder(Folder parent)
    //     {
    //        // EnsureLib(parent);
    //         // SeedFolder();
    //         ///var pathInfo = new MediaPathInfo(path);
    //        // _library.AddMediaPath(folder.Name, pathInfo);

    //         // return TryGetMovieFolder()
    //          var stremioFolder = new Folder
    //         {
    //              Name = "movies",
    //              Id = _library.GetNewItemId("movies", typeof(Folder)),
    //              // LocationType = LocationType.Virtual,
    //              IsVirtualItem = true,
    //              ParentId = parent.Id,
    //              Path = path
    //              // Path = "stremio://"
    //          };
    //         stremioFolder.PresentationUniqueKey = stremioFolder.CreatePresentationUniqueKey();
    //         // // stremioFolder.SetParentId((Guid)cfg.MovieLibraryId);
    //         // //library.CreateItem(stremioFolder);
    //         // _library.CreateItem(stremioFolder, parent);
    //         // parent.AddChild(stremioFolder);
    //         // return stremioFolder;
    //     }

    // public Folder CreateVirtualFolder(Folder parent, string Name)
    //     {
    //        // EnsureLib(parent);
    //        //  SeedFolder();
    //         ///var pathInfo = new MediaPathInfo(path);
    //        // _library.AddMediaPath(folder.Name, pathInfo);

    //         // return TryGetMovieFolder()
    //          var folder = new Folder
    //         {
    //              Name = Name,
    //              Id = Guid.NewGuid(),
    //              // LocationType = LocationType.Virtual,
    //              IsVirtualItem = true,
    //              ParentId = parent.Id,
    //              Path = path
    //              // Path = "stremio://"
    //          };
    //         folder.PresentationUniqueKey = folder.CreatePresentationUniqueKey();
    //         // // stremioFolder.SetParentId((Guid)cfg.MovieLibraryId);
    //         // //library.CreateItem(stremioFolder);
    //         // _library.CreateItem(stremioFolder, parent);
    //         parent.AddChild(folder);
    //         //_library.AddMediaPath();
    //         //_library.AddVirtualFolder("MoviesExternal", )
    //         return folder;
    //     }
    // public static Folder? GetStremioFolder(this PluginConfiguration cfg, ILibraryManager library)
    // {
    //     // return library.GetItemList(new InternalItemsQuery
    //     // {
    //     //     ParentId = parent.Id
    //     // })
    //     //     .OfType<Folder>()
    //     //     .FirstOrDefault(x => x.Name == "Stremio");
    // }



    public async Task<string?> SaveStrmAsync(
    string path,
    string url,
    bool overwrite = true,
    CancellationToken ct = default)
    {
        if (!IsValidUrl(url))
        {
            _log.LogWarning("Skipping STRM creation: invalid URL '{Url}' at {Path}", url, path);
            return null;
        }

        var target = Path.ChangeExtension(path, ".strm");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var tmp = target + ".tmp";
        var data = Encoding.UTF8.GetBytes(url.Trim() + "\n");

        await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await fs.WriteAsync(data, 0, data.Length, ct);
            await fs.FlushAsync(ct);
        }

        if (File.Exists(target))
        {
            if (!overwrite) return target;
            File.Delete(target);
        }

        File.Move(tmp, target);
        return target;
    }

    private static bool IsValidUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    public async Task<BaseItem?> WaitForMediaAsync(
        Dictionary<string, string> providerIds,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var stop = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < stop)
        {
            var q = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                Recursive = true,
                Limit = 1,
                HasAnyProviderId = providerIds
            };

            var results = _library.GetItemList(q);
            var match = results.FirstOrDefault();
            if (match != null) return match;

            await Task.Delay(250, ct);
        }
        return null;
    }



}
