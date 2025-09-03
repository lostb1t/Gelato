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
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

namespace Jellyfin.Plugin.ExternalMedia;

public class ExternalMediaManager
{
    private readonly ILogger<ExternalMediaManager> _log;
    private readonly ILoggerFactory _loggerFactory;
    // private readonly IFileSystem _fileSystem;
    private readonly ExternalMediaStremioProvider _stremioProvider;
    private readonly IServerConfigurationManager _config;
    private readonly IUserManager _user;
    private readonly ILibraryManager _library;
    private readonly IItemRepository _repo;
    private readonly IDtoService _dtoService;
    private readonly IFileSystem _fileSystem;
    private readonly IProviderManager _provider;

    // private readonly string path = "/media/external/movies";
    // private readonly string name = "external_movies";

    public ExternalMediaManager(

        ILoggerFactory loggerFactory,
        IProviderManager provider,
        ExternalMediaStremioProvider stremioProvider,
        IDtoService dtoService,
        IServerConfigurationManager config,
        IUserManager userManager,
IItemRepository repo,
          IFileSystem fileSystem,
        ILibraryManager libraryManager)
    {
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<ExternalMediaManager>();
        _provider = provider;
        _stremioProvider = stremioProvider;
        _dtoService = dtoService;
        _config = config;
        _repo = repo;
        _user = userManager;
        _library = libraryManager;
        _fileSystem = fileSystem;
    }

    public static void SeedFolder(string path)
    {
        Directory.CreateDirectory(path);
        var seed = System.IO.Path.Combine(path, "stub.txt");
        if (!File.Exists(seed))
            File.WriteAllBytes(seed, Array.Empty<byte>());
    }

    public async Task WriteAllTextAsync(string path, string contents, CancellationToken ct)
    {
        var bytes = new UTF8Encoding(false).GetBytes(contents);
        await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        await fs.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
    }

    public void DeleteStrmFiles(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return;

        var strmFiles = Directory.GetFiles(folderPath, "*.strm", SearchOption.TopDirectoryOnly);
        foreach (var file in strmFiles)
        {
            File.Delete(file);
        }
    }

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
        return _library.GetItemList(new InternalItemsQuery
        {
            Path = cfg.MoviePath
        })
            .OfType<Folder>()
            .FirstOrDefault();
    }

    public Folder? TryGetSeriesFolder()
    {
        var cfg = ExternalMediaPlugin.Instance!.Configuration;
        _log.LogDebug("Looking for series folder at {Path}", cfg.SeriesPath);
        return _library.GetItemList(new InternalItemsQuery
        {
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
            var rootQ = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                Recursive = true,
                Limit = 1,
                HasAnyProviderId = providerIds
            };

            var root = _library.GetItemList(rootQ).FirstOrDefault();

            if (root != null && await IsReadyAsync(root, ct).ConfigureAwait(false))
                return root;

            await Task.Delay(300, ct).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<bool> IsReadyAsync(BaseItem root, CancellationToken ct)
    {
        // Must have a primary image before we consider it "ready"
        if (!root.HasImage(ImageType.Primary))
            return false;

        // Movies: done
        if (root is MediaBrowser.Controller.Entities.Movies.Movie)
            return true;

        // Series: require at least one season (no episode checks)
        if (root is MediaBrowser.Controller.Entities.TV.Series series)
        {
            var seasonsQ = new InternalItemsQuery
            {
                ParentId = series.Id,
                IncludeItemTypes = new[] { BaseItemKind.Season },
                Recursive = false,
                Limit = 1
            };

            var anySeason = _library.GetItemList(seasonsQ).FirstOrDefault();
            if (anySeason is not null && !anySeason.HasImage(ImageType.Primary))
            {
                return false;
            }
            return anySeason is not null;
        }

        return true;
    }

    public async Task SaveStreamAsStrm(Folder folder, StremioMeta meta, IEnumerable<StremioStream> streams, CancellationToken ct)
    {
        var i = 1;
        foreach (var s in streams)
        {
            var r = await SaveStrmAsync(
                  $"{folder.Path}/{meta.Name} ({meta.Year})/{meta.Name} ({meta.Year}) - {i} {s.Name}",
                s.Url
            );
            if (r is null)
            {
                Console.WriteLine(JsonSerializer.Serialize(s));
            }
            i++;
        }
    }

    public void QueueParentRefresh(BaseItem item)
    {
        var parent = item.GetParent();
        if (parent == null) return;

        _log.LogInformation("ExternalMedia: queueing refresh for parent {Name}", parent.Name);

        var opts = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllImages = false,
            ReplaceAllMetadata = false,
            EnableRemoteContentProbe = false
        };

        _provider.QueueRefresh(parent.Id, opts, RefreshPriority.High);
    }

    // public BaseItem ApplyStream(BaseItem item, StremioMeta meta, StremioStream stream, Folder parent)
    // {
    //     item.ShortcutPath = stream.Url;
    //     item.IsShortcut = true;
    //     item.Path = $"{parent.Path}/{meta.Year} ({meta.Year})/{item.Name} ({meta.Year}) - {i} {s.Name}.strm";
    //     // item.Path = $"{root.Path}/{meta.Name} ({meta.Year})/{meta.Name} ({meta.Year}) - {i} {s.Name}.strm";
    //     return item;
    // }

    public Dictionary<string, string> GetProviderIds(StremioMeta meta)
    {
        var providerIds = new Dictionary<string, string>();
        var Id = meta.Id;
        if (!string.IsNullOrWhiteSpace(Id))
        {
            if (Id.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase))
            {

            }
            if (Id.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                providerIds.Add(MetadataProvider.Imdb.ToString(), Id);
            }
        }
        //item.IsRemote = true;

        providerIds.Add(MetadataProvider.Imdb.ToString(), Id);
        providerIds.Add("stremio", $"stremio://{meta.Type}/{Id}");
        return providerIds;
    }

    public async Task<List<Video>> SaveStreams(IEnumerable<StremioStream> streams, Folder parent, StremioMeta meta, CancellationToken ct)
    {
        var desiredIds = new HashSet<Guid>();
        var items = new List<Video>();
        var providerIds = GetProviderIds(meta);
        var i = 1;
        foreach (StremioStream stream in streams)
        {
            if (!stream.IsValid()) continue;
            var Id = stream.GetGuid();

            _log.LogInformation("ExternalMedia: Adding stream {Id} {Filename}", stream.GetGuid(), stream.BehaviorHints?.Filename);
            var item = new Movie()
            {
                Id = stream.GetGuid(),
                Name = stream.GetName(),
                ShortcutPath = stream.Url,
                IsShortcut = true,
                // Path = $"{parent.Path}/{meta.Name} ({meta.Year})/{Id}.strm",
                Path = $"{parent.Path}/{meta.Name} ({meta.Year})/{meta.Name} ({meta.Year}) - {i} {stream.Name}.strm",
                ParentId = parent.Id,
                ProviderIds = providerIds,
            };
            item.PresentationUniqueKey = item.CreatePresentationUniqueKey();

            await SaveStrmAsync(
                item.Path,
                stream.Url
            );

            //root.AddChild(baseItem);
            items.Add(item as Video);
            i++;
        }
        var ids = new HashSet<Guid>(items.Select(x => x.Id));

        var stale = _library.GetItemList(new InternalItemsQuery
        {
            ParentId = parent.Id,
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            HasAnyProviderId = providerIds,
            Recursive = false
        })
        .OfType<Video>()
        .Where(m => !ids.Contains(m.Id))
        .ToList();

        foreach (var m in stale)
        {
            _library.DeleteItem(
                m,
                new DeleteOptions { DeleteFileLocation = true },
                true
            );
        }

        if (items.Any())
        {
            var item = items[0];
            // {
            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ForceSave = true
            };
            await item.RefreshMetadata(options, CancellationToken.None);

            await MergeVersions(items.ToArray());
        }

        return items;

    }

    public Video? GetPrimaryVersion(List<Video> items)
    {
        return items.FirstOrDefault(i => i.MediaSourceCount > 1 && string.IsNullOrEmpty(i.PrimaryVersionId));
    }

    public async Task MergeVersions(Video[] items)
    {
        if (!items.Any()) return;
        var primaryVersion = items.FirstOrDefault(i => i.MediaSourceCount > 1 && string.IsNullOrEmpty(i.PrimaryVersionId));
        _log.LogInformation("Merging {Count} versions, primary is {Primary}", items.Length, primaryVersion?.Id);
        if (primaryVersion is null)
        {
            primaryVersion = items.First();
        }
        //  _log.LogInformation("Chosen primary version is {Primary}", primaryVersion.Id);
        var alternateVersionsOfPrimary = primaryVersion.LinkedAlternateVersions.ToList();

        foreach (var item in items)
        {
            _log.LogInformation("Item {Item} has {Media} media sources", item.Id, item.Name);
        }
        foreach (var item in items.Where(i => !i.Id.Equals(primaryVersion.Id)))
        {
            _log.LogInformation("Setting primary version of {Item} to {Primary}", item.Id, primaryVersion.Id);
            item.SetPrimaryVersionId(primaryVersion.Id.ToString("N", CultureInfo.InvariantCulture));

            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);

            if (!alternateVersionsOfPrimary.Any(i => string.Equals(i.Path, item.Path, StringComparison.OrdinalIgnoreCase)))
            {
                //     _log.LogInformation("Adding {Alt} as alternate version to {Primary}", item.Id, primaryVersion.Id);
                alternateVersionsOfPrimary.Add(new LinkedChild
                {
                    Path = item.Path,
                    ItemId = item.Id
                });
            }

            foreach (var linkedItem in item.LinkedAlternateVersions)
            {
                if (!alternateVersionsOfPrimary.Any(i => string.Equals(i.Path, linkedItem.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    //  _log.LogInformation("Adding {Alt} as alternate version to {Primary}", linkedItem.ItemId, primaryVersion.Id);
                    alternateVersionsOfPrimary.Add(linkedItem);
                }
            }

            if (item.LinkedAlternateVersions.Length > 0)
            {
                item.LinkedAlternateVersions = Array.Empty<LinkedChild>();
                _log.LogInformation("Cleared alternate versions from {Item}", item.Id);
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
            }
        }

        primaryVersion.LinkedAlternateVersions = alternateVersionsOfPrimary.ToArray();
        await primaryVersion.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
    }

}
