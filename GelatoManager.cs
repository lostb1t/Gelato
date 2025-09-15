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
using Gelato.Configuration;
using Gelato.Common;
using MediaBrowser.Model.Configuration;
using System.Text;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Providers;          // MetadataRefreshOptions, MetadataRefreshMode, DirectoryService
using MediaBrowser.Model.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Caching.Memory;


namespace Gelato;

public class GelatoManager
{
    private readonly ILogger<GelatoManager> _log;
    private readonly ILoggerFactory _loggerFactory;
    // private readonly IFileSystem _fileSystem;
    private readonly GelatoStremioProvider _stremioProvider;
    private readonly IServerConfigurationManager _config;
    private readonly IUserManager _user;
    private readonly ILibraryManager _library;
    private readonly IItemRepository _repo;
    private readonly IDtoService _dtoService;
    private readonly IFileSystem _fileSystem;
    private readonly IProviderManager _provider;
    private readonly IMemoryCache _memoryCache;


    public GelatoManager(
        ILoggerFactory loggerFactory,
        IProviderManager provider,
        GelatoStremioProvider stremioProvider,
        IDtoService dtoService,
        IServerConfigurationManager config,
        IUserManager userManager,
        IItemRepository repo,
        IFileSystem fileSystem,
        IMemoryCache memoryCache,
        ILibraryManager libraryManager)
    {
        _loggerFactory = loggerFactory;
        _memoryCache = memoryCache;
        _log = loggerFactory.CreateLogger<GelatoManager>();
        _provider = provider;
        _stremioProvider = stremioProvider;
        _dtoService = dtoService;
        _config = config;
        _repo = repo;
        _user = userManager;
        _library = libraryManager;
        _fileSystem = fileSystem;
    }

    public void SaveStremioUri(Guid guid, StremioUri stremioUri)
    {
        _memoryCache.Set($"uri:{guid}", stremioUri, TimeSpan.FromMinutes(3600));
    }

    public StremioUri? GetStremioUri(Guid guid)
    {
        return _memoryCache.TryGetValue($"uri:{guid}", out var value) ? value as StremioUri : null;
    }
    
    public void SetStreamSync(Guid guid)
    {
        _memoryCache.Set($"streamsync:{guid}", guid, TimeSpan.FromMinutes(3600));
    }

    public bool HasStreamSync(Guid guid)
{
    return _memoryCache.TryGetValue($"streamsync:{guid}", out _);
}

    public void SaveStremioMeta(Guid guid, StremioMeta meta)
    {
        // _log.LogInformation("Caching StremioMeta {Guid} {Name} ({Year})", guid, meta.Name, meta.Year);
        _memoryCache.Set($"meta:{guid}", meta, TimeSpan.FromMinutes(360));
    }

    public StremioMeta? GetStremioMeta(Guid guid)
    {
        // _log.LogInformation("Retrieving StremioMeta for {Guid}", guid);
        return _memoryCache.TryGetValue($"meta:{guid}", out var value) ? value as StremioMeta : null;
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
        var cfg = GelatoPlugin.Instance!.Configuration;
        if (cfg.MoviePath is null)
        {
            return null;
        }

        SeedFolder(cfg.MoviePath);
        return _library.GetItemList(new InternalItemsQuery
        {
            Path = cfg.MoviePath
        })
            .OfType<Folder>()
            .FirstOrDefault();

    }

    public Folder? TryGetSeriesFolder()
    {
        var cfg = GelatoPlugin.Instance!.Configuration;
        if (cfg.SeriesPath is null)
        {
            return null;
        }

        SeedFolder(cfg.SeriesPath);
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

    /// <summary>
    /// Inserts metadata into the library. Skip if exists
    /// </summary>
    /// <param name="meta"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public async Task<(BaseItem?, bool)> InsertMeta(Folder parent, StremioMeta meta, bool refreshItem, CancellationToken ct)
    {
        if (meta.Type != StremioMediaType.Movie && meta.Type != StremioMediaType.Series)
        {
            return (null, false);
        }

        var baseItem = _stremioProvider.IntoBaseItem(meta);
        if (baseItem is null || baseItem.ProviderIds is null || baseItem.ProviderIds.Count == 0)
        {
            _log.LogWarning("Gelato: Missing provider ids, skipping");
            return (null, false);
        }

        //var found = FindByStremioId(meta.Id) as Video;
        var found = FindByProviderIds(baseItem.ProviderIds, baseItem.GetBaseItemKind());
        if (found is not null)
        {
            return (found, false);
        }
        //baseItem.IsDefault = true;

        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ForceSave = true
        };

        if (meta.Type == StremioMediaType.Movie)
        {
            parent.AddChild(baseItem);
        }

        if (meta.Type == StremioMediaType.Series)
        {
            baseItem = await CreateSeriesTreesAsync(parent, meta, ct);
        }
        
        _log.LogInformation($"inserted new media: {baseItem.Name}");
        if (refreshItem && baseItem is not null)
        {
            _provider.QueueRefresh(
                    baseItem.Id,
                                 new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                                 {
                                     MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                                     ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                                     ForceSave = true
                                 },
                                  RefreshPriority.High);
        }
        return (baseItem as BaseItem, true);
    }

    public Video? FindByStremioId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var lastSlash = id.LastIndexOf('/');
        var ext = lastSlash >= 0 ? id[(lastSlash + 1)..] : id;

        string providerKey;
        string providerValue;

        if (ext.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase))
        {
            providerKey = MetadataProvider.Tmdb.ToString();
            providerValue = ext.Substring("tmdb:".Length);
        }
        else if (ext.StartsWith("imdb:", StringComparison.OrdinalIgnoreCase))
        {
            providerKey = MetadataProvider.Imdb.ToString();
            providerValue = ext.Substring("imdb:".Length);
        }
        else if (ext.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            providerKey = MetadataProvider.Imdb.ToString();
            providerValue = ext;
        }
        else
        {
            return null;
        }

        var q = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            Limit = 1,
            HasAnyProviderId = new Dictionary<string, string> { [providerKey] = providerValue }
        };
        return _library.GetItemList(q).OfType<Video>().FirstOrDefault(i => i.MediaSourceCount > 1 && string.IsNullOrEmpty(i.PrimaryVersionId));
        //return _library.GetItemList(q).OfType<Video>().FirstOrDefault(i => i.MediaSourceCount > 1 && string.IsNullOrEmpty(i.PrimaryVersionId));
    }

    public Video? FindByProviderIds(Dictionary<string, string> providerIds, BaseItemKind kind )
    {
      
        var q = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            Recursive = true,
            Limit = 1,
            HasAnyProviderId = providerIds
        };

        return _library.GetItemList(q).OfType<Video>().FirstOrDefault();
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

        _log.LogInformation("Gelato: queueing refresh for parent {Name}", parent.Name);

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
 


public async Task<List<Video>> SyncStreams(BaseItem item, CancellationToken ct)
{
    var isEpisode = item.GetBaseItemKind() == BaseItemKind.Episode;
    var parent = isEpisode ? TryGetSeriesFolder() : TryGetMovieFolder();
    if (parent is null) return new List<Video>();
    if (!item.Path.StartsWith("stremio:", StringComparison.OrdinalIgnoreCase)) return new List<Video>();

    var providerIds = item.ProviderIds ?? new Dictionary<string, string>();
    var streams = await _stremioProvider.GetStreamsAsync(item).ConfigureAwait(false);

    var current = _library.GetItemList(new InternalItemsQuery
    {
        ParentId = parent.Id,
        IncludeItemTypes = new[] { item.GetBaseItemKind() },
        HasAnyProviderId = providerIds,
        Recursive = false
    })
    .OfType<Video>()
    .ToList();

    var currentById = current.ToDictionary(v => v.Id, v => v);
    var currentIds  = new HashSet<Guid>(current.Select(v => v.Id));

    var desiredIds = new HashSet<Guid> { item.Id };
    var created = new List<Video>();
    if (item is Video baseVideo) created.Add(baseVideo);

    var i = 0;
    foreach (var s in streams)
    {
        if (!s.IsValid()) continue;
        i++;

        var id = s.GetGuid();
        if (!desiredIds.Add(id)) continue;


        var sort  = $"BB{i.ToString("D3")}";
        var label = $"{s.GetName()}";

        if (currentIds.Contains(id))
        {
            var existing = currentById[id];
            
            var locked = existing.LockedFields?.ToList() ?? new List<MetadataField>();

    var _ = locked.Remove(MetadataField.Name);
    existing.LockedFields = locked.ToArray();
    await existing.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct);

            existing.Name = label;
            existing.SortName = sort;
            existing.ForcedSortName = sort;

            if (!locked.Contains(MetadataField.Name)) locked.Add(MetadataField.Name);
            existing.LockedFields = locked.ToArray();

            await existing.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
            continue;
        }

        Video child = isEpisode ? new Episode { Id = id } : new Movie { Id = id };

        child.Name = label;
        child.IsVirtualItem = false;
        child.ProviderIds = providerIds;
        child.Path = s.Url;
        child.RunTimeTicks = item.RunTimeTicks;
        child.SortName = sort;
        child.ForcedSortName = sort;

        var lockedNew = child.LockedFields?.ToList() ?? new List<MetadataField>();
        if (!lockedNew.Contains(MetadataField.Name))    lockedNew.Add(MetadataField.Name);
        child.LockedFields = lockedNew.ToArray();

        parent.AddChild(child);
        await child.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);

        created.Add(child);
    }

    // delete stale
    var stale = current.Where(m => !desiredIds.Contains(m.Id)).ToList();
    foreach (var m in stale) {
      if (m.Id != item.Id) {
        _library.DeleteItem(m, new DeleteOptions { DeleteFileLocation = false }, true);
      }
      }

    // merge
    //if (created.Count > 0 || desiredIds.Count > 1)
    //{
        var keep = current.Where(m => desiredIds.Contains(m.Id));
        var merged = created.Concat(keep).GroupBy(v => v.Id).Select(g => g.First()).ToArray();
        await MergeVersions(merged).ConfigureAwait(false);
    //}

    return created;
}

    public Video? GetPrimaryVersion(List<Video> items)
    {
        return items.FirstOrDefault(i => i.MediaSourceCount > 1 && string.IsNullOrEmpty(i.PrimaryVersionId));
    }

    public async Task MergeVersions(Video[] items)
{
    if (items == null || items.Length < 2)
    {
        _log.LogWarning("MergeVersions called with insufficient items.");
        return;
    }

    var primaryVersion = items.FirstOrDefault(
        i => i.Path?.StartsWith("stremio", StringComparison.OrdinalIgnoreCase) == true);
        _log.LogInformation($"{primaryVersion.Path}");
    if (primaryVersion == null)
    {
        _log.LogError("MergeVersions: No item with a path starting with 'stremio' found. Merge aborted.");
        return;
    }

    var opts = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
    {
        MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
        ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
        ReplaceAllImages = false,
        ReplaceAllMetadata = false,
        EnableRemoteContentProbe = false,
        ForceSave = true
    };

    var inv = CultureInfo.InvariantCulture;
    var alternates = items.Where(i => !i.Id.Equals(primaryVersion.Id)).ToList();
    var replacementLinks = alternates
        .Select(i => new LinkedChild { Path = i.Path, ItemId = i.Id })
        .ToArray();

    foreach (var v in alternates)
    {
        v.SetPrimaryVersionId(primaryVersion.Id.ToString("N", inv));
        v.LinkedAlternateVersions = Array.Empty<LinkedChild>();

        await v.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
               .ConfigureAwait(false);

       // v.RefreshMetadata(opts, CancellationToken.None).GetAwaiter().GetResult();
    }

    primaryVersion.LinkedAlternateVersions = replacementLinks;
    primaryVersion.SetPrimaryVersionId(null);
    await primaryVersion.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
                       .ConfigureAwait(false);

   // primaryVersion.RefreshMetadata(opts, CancellationToken.None).GetAwaiter().GetResult();
}
    
    
    private void AddToAlternateVersionsIfNotPresent(List<LinkedChild> alternateVersions,
                                                        LinkedChild newVersion)
    {
        if (!alternateVersions.Any(
            i => string.Equals(i.Path,
                            newVersion.Path,
                            StringComparison.OrdinalIgnoreCase
                        )))
        {
            alternateVersions.Add(newVersion);
        }
    }

    public bool IsItemsAction(ActionContext ctx)
    {
        if (ctx.ActionDescriptor is not ControllerActionDescriptor cad)
            return false;

        var name = cad.ActionName;
        return IsItemsActionName(name);
    }
    
    public bool IsItemsActionName(string name)
    {
        return string.Equals(name, "GetItems", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "GetItem", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "GetItemLegacy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "GetItemsByUserIdLegacy", StringComparison.OrdinalIgnoreCase);
    }

    public bool TryGetRouteGuid(ActionContext ctx, out Guid value)
    {
        value = Guid.Empty;
        if (TryGetRouteGuidString(ctx, out var s) && Guid.TryParse(s, out var g))
        {
            value = g;
            return true;
        }
        return false;
    }

    public bool IsStremioProvider(BaseItem item)
    {

        if (!string.IsNullOrWhiteSpace(item.Path) && item.Path.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public bool TryGetRouteGuidString(ActionContext ctx, out string value)
    {
        value = "";

        var rd = ctx.RouteData.Values;

        // in flight changing for query items is not allowed
        if (ctx.HttpContext.Items["GuidResolved"] is Guid g)
        {
            value = g.ToString("N");
            return true;
        }

        foreach (var key in new[] { "id", "Id", "ID", "itemId", "ItemId", "ItemID" })
        {
            if (rd.TryGetValue(key, out var raw) && raw is not null)
            {
                var s = raw.ToString();
                // _log.LogDebug("Checking route param {Key} = '{Value}'", key, s);
                if (!string.IsNullOrWhiteSpace(s))
                {
                    value = s;
                    return true;
                }
            }
        }

        // Fallback: check query string "ids"
        var query = ctx.HttpContext.Request.Query;
        if (query.TryGetValue("ids", out var ids) && ids.Count == 1)
        {
            var s = ids[0];
            if (!string.IsNullOrWhiteSpace(s))
            {
                value = s;
                return true;
            }
        }

        return false;
    }

    public void ReplaceGuid(ActionContext ctx, Guid value)
    {
        // Replace route values
        var rd = ctx.RouteData.Values;
        foreach (var key in new[] { "id", "Id", "ID", "itemId", "ItemId", "ItemID" })
        {
            if (rd.TryGetValue(key, out var raw) && raw is not null)
            {
                // _log.LogInformation("Gelato: Replacing route {Key} {Old} → {New}", key, raw, value);
                ctx.RouteData.Values[key] = value.ToString();
            }
        }

        // Replace query string "ids"
        var request = ctx.HttpContext.Request;
        var parsed = QueryHelpers.ParseQuery(request.QueryString.Value ?? "");

        if (parsed.TryGetValue("ids", out var existing) && existing.Count == 1)
        {
            // _log.LogInformation("Gelato: Replacing query ids {Old} → {New}", existing[0], value);

            var dict = new Dictionary<string, StringValues>(parsed)
            {
                ["ids"] = new StringValues(value.ToString())
            };

            ctx.HttpContext.Request.QueryString = QueryString.Create(dict);
        }
    }

    public async Task<BaseItem?> CreateSeriesTreesAsync(
           Folder seriesRootFolder,
           StremioMeta seriesMeta,
           CancellationToken ct)
    {
        if (seriesRootFolder is null || string.IsNullOrWhiteSpace(seriesRootFolder.Path))
            return null;

        var providerIds = seriesMeta.GetProviderIds();
        if (providerIds is null || providerIds.Count == 0)
            return null;

        var groups = (seriesMeta.Videos ?? Enumerable.Empty<StremioMeta>())
            .OrderBy(e => e.Season)
            .ThenBy(e => e.Episode)
            .GroupBy(e => e.Season)
            .ToList();
        if (groups.Count == 0)
            return null;

        //var seriesStremioUri = StremioUri.LoadFromString(stremioKey);
        var seriesItem = _library.GetItemList(new InternalItemsQuery
        {
            ParentId = seriesRootFolder.Id,
            HasAnyProviderId = GetProviderIds(seriesMeta),
            Recursive = false
        })
            .OfType<Series>()
            .FirstOrDefault();
        // var seriesItem = _library.FindByPath(seriesPath, true) as Series;
        if (seriesItem is null)
        {
            seriesItem = (Series)_stremioProvider.IntoBaseItem(seriesMeta);
            if (seriesItem.Id == Guid.Empty) seriesItem.Id = Guid.NewGuid();
            // seriesItem.Path =  "stremio://{meta.Type}/{Id}";;
            seriesItem.PresentationUniqueKey = seriesItem.CreatePresentationUniqueKey();
            seriesRootFolder.AddChild(seriesItem);
            //         _provider.QueueRefresh(seriesItem.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.High);
        }

        //_log.LogInformation($"syncing series {seriesItem.Name}");

        foreach (var seasonGroup in groups)
        {
            ct.ThrowIfCancellationRequested();
            var seasonIndex = seasonGroup.Key;
            var seasonPath = $"{seriesItem.Path}:{seasonIndex}";

            var seasonItem = _library.FindByPath(seasonPath, true) as Season;
            if (seasonItem is null)
            {
                // _log.LogInformation($"Gelato: creating series {seriesItem.Name} season {seasonIndex:D2}");
                seasonItem = new Season
                {
                    Id = Guid.NewGuid(),
                    Name = $"Season {seasonIndex:D2}",
                    IndexNumber = seasonIndex,
                    //  ParentId = seriesItem.Id,
                    SeriesId = seriesItem.Id,
                    SeriesName = seriesItem.Name,
                    Path = seasonPath,
                    SeriesPresentationUniqueKey = seriesItem.GetPresentationUniqueKey()
                };
                seasonItem.SetProviderId("stremio", seasonItem.Path);
                seriesItem.AddChild(seasonItem);
                //_provider.QueueRefresh(seasonItem.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.High);
            }

            var existing = _library.GetItemList(new InternalItemsQuery
            {
                ParentId = seasonItem.Id,
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                Recursive = false
            })
            .OfType<Episode>()
            .ToDictionary(e => e.Path, e => e);

            var desiredIds = new HashSet<Guid>();

            // _log.LogInformation($"Gelato: syncing {existing.Count()}");

            foreach (var epMeta in seasonGroup)
            {
                ct.ThrowIfCancellationRequested();

                // _log.LogInformation($"Gelato: syncing series episode {seriesItem.Name} S{epMeta.Season:D2}E{epMeta.Episode:D2} {epMeta.Name}");
                var epPath = $"{seasonItem.Path}:{epMeta.Number}";

                if (existing.GetValueOrDefault(epPath) is not null)
                {
                    // _log.LogInformation($"Gelato: episode already exists, skipping");
                    continue;
                }
                // _log.LogInformation($"Gelato: creating series episode {seriesItem.Name} S{epMeta.Season:D2}E{epMeta.Number:D2} {epMeta.Name}");
                var epItem = new Episode
                {
                    Id = Guid.NewGuid(),
                    Name = epMeta.Name,
                    IndexNumber = epMeta.Number,
                    ParentIndexNumber = epMeta.Season,
                    //  ParentId = seasonItem.Id,
                    SeasonId = seasonItem.Id,
                    SeriesId = seriesItem.Id,
                    SeriesName = seriesItem.Name,
                    SeasonName = seasonItem.Name,
                    Path = epPath,
                    SeriesPresentationUniqueKey = seasonItem.SeriesPresentationUniqueKey,

                };

                epItem.PresentationUniqueKey = epItem.GetPresentationUniqueKey();
                epItem.SetProviderId("stremio", epItem.Path);
                seasonItem.AddChild(epItem);
                //    _provider.QueueRefresh(epItem.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.High); 

                //            _provider.QueueRefresh(seasonItem.Id, new MetadataRefreshOptions(new DirectoryService(_fileSystem)), RefreshPriority.High);
                //     await seasonItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct);
                //await epItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct);
                //await seriesItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct);


                // _repo.SaveItems(new BaseItem[] { epItem }, ct);
            }
        }


        // _log.LogInformation($"Gelato: done sync series");
        return seriesItem;
    }


}
