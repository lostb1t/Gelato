#pragma warning disable SA1611
#pragma warning disable SA1591
#pragma warning disable SA1615
#pragma warning disable CS0165

using System.Collections.Generic;
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
using Gelato.Common;
using System.Text;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using System.Text.Json;
using System.Globalization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Caching.Memory;
using MediaBrowser.Model.Dto;
using Jellyfin.Database.Implementations.Entities;

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

    public void SetStremioSubtitlesCache(Guid guid, List<StremioSubtitle> subs)
    {
        _memoryCache.Set($"subs:{guid}", subs, TimeSpan.FromMinutes(3600));
    }

    public List<StremioSubtitle>? GetStremioSubtitlesCache(Guid guid)
    {
        return _memoryCache.Get<List<StremioSubtitle>>($"subs:{guid}");
    }

    public void SaveStremioUri(Guid guid, StremioUri stremioUri)
    {
        _memoryCache.Set($"uri:{guid}", stremioUri, TimeSpan.FromMinutes(3600));
    }

    public StremioUri? GetStremioUri(Guid guid)
    {
        return _memoryCache.TryGetValue($"uri:{guid}", out var value) ? value as StremioUri : null;
    }

    public void SetStreamSync(string key)
    {
        _memoryCache.Set($"streamsync:{key}", key, TimeSpan.FromMinutes(3600));
    }

    public bool HasStreamSync(string key)
    {
        return _memoryCache.TryGetValue($"streamsync:{key}", out _);
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

    public void RemoveStremioMeta(Guid guid)
    {
        _memoryCache.Remove($"meta:{guid}");
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

    private static bool IsValidUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>
    /// Inserts metadata into the library. Skip if exists
    /// </summary>
    /// <param name="meta"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public async Task<(BaseItem?, bool)> InsertMeta(Folder parent, StremioMeta meta, bool refreshItem, bool queueRefreshItem, CancellationToken ct)
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
        var found = GetByProviderIds(baseItem.ProviderIds, baseItem.GetBaseItemKind());
        if (found is not null)
        {
            _log.LogDebug($"InsertMeta: found existing item: {found.Id}");
            return (found, false);
        }
        //baseItem.IsDefault = true;

        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllImages = false,
            ReplaceAllMetadata = true,
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


        if (baseItem is not null)
        {
            _log.LogInformation($"inserted new media: {baseItem.Name}");
            if (queueRefreshItem)
            {
                _log.LogDebug($"InsertMeta: queue refresh for: {baseItem.Id}");
                _provider.QueueRefresh(
                        baseItem.Id,
                          options,
                                      RefreshPriority.High);
            }
            else if (refreshItem)
            {
                _log.LogDebug($"InsertMeta: refresh for: {baseItem.Id}");
                await _provider.RefreshFullItem(baseItem, options, ct);
            }
        }
        return (baseItem as BaseItem, true);
    }
    
    public async Task<BaseItem?> WaitForInsert(StremioMeta meta) {
            var tempItem = _stremioProvider.IntoBaseItem(meta);
            var timeout = TimeSpan.FromSeconds(10);
            var interval = TimeSpan.FromSeconds(1);
            var start = DateTime.UtcNow;
        BaseItem? baseItem = null;
            _log.LogDebug("Insert threw; assuming race. Waiting for item to materialize.");
            while (DateTime.UtcNow - start < timeout)
            {
                baseItem = GetByProviderIds(tempItem.ProviderIds, tempItem.GetBaseItemKind());
                if (baseItem != null)
                {
                    _log.LogDebug("Found item after race.");
                    break;
                }

                await Task.Delay(interval).ConfigureAwait(false);
            }
            return baseItem;
        }


    public IEnumerable<BaseItem> FindByProviderIds(Dictionary<string, string> providerIds, BaseItemKind kind)
    {
        providerIds.Remove(MetadataProvider.TmdbCollection.ToString());
        
        var q = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            Recursive = true,
            HasAnyProviderId = providerIds,
            GroupByPresentationUniqueKey = false,
            GroupBySeriesPresentationUniqueKey = false,
            CollapseBoxSetItems = false
        };

        return _library.GetItemList(q).OfType<BaseItem>();
    }

    public BaseItem? GetByProviderIds(Dictionary<string, string> providerIds, BaseItemKind kind) {
      return FindByProviderIds(providerIds, kind).FirstOrDefault();
  }
  
  public BaseItem? GetExisting(StremioMeta meta) {
    var item = _stremioProvider.IntoBaseItem(meta);
    return GetByProviderIds(item.ProviderIds, item.GetBaseItemKind());
  }

    public async Task<List<Video>> SyncStreams(BaseItem item, CancellationToken ct)
    {
        _log.LogDebug($"SyncStreams for {item.Id}");
        Episode? baseEp = item as Episode;
        bool isEpisode = baseEp is not null;
        var parent = isEpisode ? item.GetParent() as Folder : TryGetMovieFolder();
        if (parent is null) return new List<Video>();

        var providerIds = item.ProviderIds ?? new Dictionary<string, string>();
        var uri = StremioUri.FromBaseItem(item);
        if (uri is null)
        {
            _log.LogError($"unable to build stremio uri for {item.Name}");
            return new List<Video>();
        }

        var streams = await _stremioProvider.GetStreamsAsync(uri).ConfigureAwait(false);
        
        // item could be a local file which does not have the stremio marker
        if (!providerIds.ContainsKey("stremio"))
        {
            providerIds["Stremio"] = uri.ExternalId;
        }
        //Console.WriteLine(string.Join(", ", providerIds.Select(kvp => $"{kvp.Key}={kvp.Value}")));
        var query = new InternalItemsQuery
        {
            ParentId = parent.Id,
            IncludeItemTypes = new[] { isEpisode ? BaseItemKind.Episode : BaseItemKind.Movie },
            HasAnyProviderId = new() { { "Stremio", providerIds["Stremio"] } },
// HasAnyProviderId = providerIds,
            Recursive = false,
            GroupByPresentationUniqueKey = false,
            GroupBySeriesPresentationUniqueKey = false,
            CollapseBoxSetItems = false
        };

        var current = _library.GetItemList(query)
    .OfType<Video>()
    .Where(v => !v.IsFileProtocol)
    .ToList();

        var currentById = current.ToDictionary(v => v.Id, v => v);
        var currentIds = new HashSet<Guid>(current.Select(v => v.Id));
        var desiredIds = new HashSet<Guid> { item.Id };

        var created = new List<Video>();
        if (item is Video baseVideo) created.Add(baseVideo);

        var i = 0;
        foreach (var s in streams)
        {
            if (!s.IsValid()) continue;
            i++;

            var id = s.GetGuid();
            if (!desiredIds.Add(id)) continue; // already accounted for

            var sort = $"BB{i:D3}";
            var label = $"{item.Name} - {s.Name}";

            if (currentIds.Contains(id))
            {
                var existing = currentById[id];

                // Temporarily unlock Name to update it
                var locked = existing.LockedFields?.ToList() ?? new List<MetadataField>();
                _ = locked.Remove(MetadataField.Name);
                existing.LockedFields = locked.ToArray();
                await existing.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);

                existing.Name = label;
                //  existing.SortName = sort;
                existing.ForcedSortName = sort;
                // existing.SetProviderId("altver", 1);

                if (!locked.Contains(MetadataField.Name)) locked.Add(MetadataField.Name);
                existing.LockedFields = locked.ToArray();

                await existing.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
                continue;
            }

            Video child;
            if (isEpisode && baseEp is not null)
            {
                var ep = new Episode { Id = id };
                ep.SeriesId = baseEp.SeriesId;
                ep.SeriesName = baseEp.SeriesName;
                ep.SeasonId = baseEp.SeasonId;
                ep.SeasonName = baseEp.SeasonName;
                ep.IndexNumber = baseEp.IndexNumber;                 // E number
                ep.ParentIndexNumber = baseEp.ParentIndexNumber;     // S number
                ep.PremiereDate = baseEp.PremiereDate;

                child = ep;
            }
            else
            {
                child = new Movie { Id = id };
            }

            child.Name = label;
            child.IsVirtualItem = false;
            child.ProviderIds = providerIds;
            child.Path = s.Url;
            child.RunTimeTicks = item.RunTimeTicks;
            // child.SortName = sort;
            child.ForcedSortName = sort;
            //child.SetProviderId("altver", 1);

            var lockedNew = child.LockedFields?.ToList() ?? new List<MetadataField>();
            if (!lockedNew.Contains(MetadataField.Name)) lockedNew.Add(MetadataField.Name);
            child.LockedFields = lockedNew.ToArray();

            parent.AddChild(child);
            await child.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);

            created.Add(child);
        }

        // Delete stale children (but don't touch the base item)
        var stale = current.Where(m => !desiredIds.Contains(m.Id)).ToList();
        foreach (var m in stale)
        {
            if (m.Id != item.Id)
            {
                _log.LogDebug($"deleting {m.Name}");
                Console.WriteLine(string.Join(", ", m.ProviderIds.Select(kvp => $"{kvp.Key}={kvp.Value}")));
                _library.DeleteItem(m, new DeleteOptions { DeleteFileLocation = false }, true);
            }
        }

        var keep = current.Where(m => desiredIds.Contains(m.Id));
        var merged = created.Concat(keep).GroupBy(v => v.Id).Select(g => g.First()).ToArray();

        _log.LogDebug($"SyncStreams finished for {item.Id}");
        return merged.ToList();
    }

    public Video? GetPrimaryVersion(List<Video> items)
    {
        return items.FirstOrDefault(i => i.MediaSourceCount > 1 && string.IsNullOrEmpty(i.PrimaryVersionId));
    }
    
    public bool IsStremioPlaceholder(BaseItem item)
{
    return item.Path?.StartsWith("stremio", StringComparison.OrdinalIgnoreCase) == true;
}

    public async Task MergeVersions(Video[] items)
    {
        if (items == null || items.Length < 2)
        {
            _log.LogWarning("MergeVersions called with insufficient items.");
            return;
        }

        // try to get a persistsnt value
        var primaryVersion =
        items.FirstOrDefault(i => i.Path?.StartsWith("stremio", StringComparison.OrdinalIgnoreCase) == true)
        ?? items.FirstOrDefault(i => i.IsFileProtocol)
        ?? items.FirstOrDefault(i => i.MediaSourceCount > 1 && string.IsNullOrEmpty(i.PrimaryVersionId))
        ?? items.FirstOrDefault();

        if (primaryVersion == null)
        {
            _log.LogError("MergeVersions: No item with a path starting with 'stremio' found. Merge aborted.");
            return;
        }

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
        }

        primaryVersion.LinkedAlternateVersions = replacementLinks;
        primaryVersion.SetPrimaryVersionId(null);
        
        await primaryVersion.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
                           .ConfigureAwait(false);
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

    /// <summary>
    /// We only check permissions cause jellyfin excludes remote items by default
    /// </summary>
    /// <param name="item"></param>
    /// <param name="user"></param>
    /// <returns></returns>
    public virtual bool CanDelete(BaseItem item, User user)
    {
        var allCollectionFolders = _library.GetUserRootFolder().Children.OfType<Folder>().ToList();

        return item.IsAuthorizedToDelete(user, allCollectionFolders);
    }

    public bool IsStremioProvider(BaseItem item)
    {

        if (!string.IsNullOrWhiteSpace(item.Path) && item.Path.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public bool IsStremio(BaseItem item)
    {

        var stremioId = item.GetProviderId("Stremio");
        if (!string.IsNullOrWhiteSpace(stremioId) && !item.IsFileProtocol)
            return true;
        return false;
    }

    public bool IsStremio(BaseItemDto dto)
    {
        // var IsRemote = dto.MediaSources?.Any(ms => ms.IsRemote) == true;
        var stremioId = dto.GetProviderId("Stremio");
        if (!string.IsNullOrWhiteSpace(stremioId) && dto.LocationType == LocationType.Remote)
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
        if (seriesRootFolder is null || string.IsNullOrWhiteSpace(seriesRootFolder.Path)){
             _log.LogWarning($"seriesRootFolder null or empty for {seriesMeta.Id}");
            return null;
}
        var providerIds = seriesMeta.GetProviderIds();
        if (providerIds is null || providerIds.Count == 0){
             _log.LogWarning($"no providers found for {seriesMeta.Id}");
            return null;
}
        var groups = (seriesMeta.Videos ?? Enumerable.Empty<StremioMeta>())
            .OrderBy(e => e.Season)
            .ThenBy(e => e.Episode)
            .GroupBy(e => e.Season)
            .ToList();
        if (groups.Count == 0) {
             _log.LogWarning($"no episodes found for {seriesMeta.Id}");
            return null;
          }

        //var seriesStremioUri = StremioUri.LoadFromString(stremioKey);
        var tmpSeries = (Series)_stremioProvider.IntoBaseItem(seriesMeta);
        var seriesItem = (Series)GetByProviderIds(tmpSeries.ProviderIds, tmpSeries.GetBaseItemKind());
        if (seriesItem is null)
        {
            seriesItem = tmpSeries;
           // seriesItem = (Series)_stremioProvider.IntoBaseItem(seriesMeta);
            if (seriesItem.Id == Guid.Empty) seriesItem.Id = Guid.NewGuid();
            seriesItem.PresentationUniqueKey = seriesItem.CreatePresentationUniqueKey();
            seriesRootFolder.AddChild(seriesItem);
        }

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
                    SeriesId = seriesItem.Id,
                    SeriesName = seriesItem.Name,
                    Path = seasonPath,
                    SeriesPresentationUniqueKey = seriesItem.GetPresentationUniqueKey()
                };
                seasonItem.SetProviderId("Stremio", $"{seriesItem.GetProviderId("Stremio")}:{seasonIndex}");
                seriesItem.AddChild(seasonItem);
                await seasonItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct);
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

            foreach (var epMeta in seasonGroup)
            {
                ct.ThrowIfCancellationRequested();

                var epPath = $"{seasonItem.Path}:{epMeta.Number}";

                if (existing.GetValueOrDefault(epPath) is not null)
                {
                    continue;
                }

                var epItem = new Episode
                {
                    Id = Guid.NewGuid(),
                    Name = epMeta.Name,
                    IndexNumber = epMeta.Episode,
                    ParentIndexNumber = epMeta.Season,
                    SeasonId = seasonItem.Id,
                    SeriesId = seriesItem.Id,
                    SeriesName = seriesItem.Name,
                    SeasonName = seasonItem.Name,
                    Path = epPath,
                    SeriesPresentationUniqueKey = seasonItem.SeriesPresentationUniqueKey,
                };

                epItem.PresentationUniqueKey = epItem.GetPresentationUniqueKey();
                //epItem.SetProviderId("Stremio", epItem.Path);
                epItem.SetProviderId("Stremio", $"{seasonItem.GetProviderId("Stremio")}:{epMeta.Number}");
                seasonItem.AddChild(epItem);
                await epItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct);
            }
        }

        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.None,
            ImageRefreshMode = MetadataRefreshMode.None,
            ReplaceAllImages = false,
            ReplaceAllMetadata = false,
            ForceSave = true
        };

        // Refresh just the parent show so children are re-scanned
        await _provider.RefreshFullItem(seriesItem, options, CancellationToken.None);
        await seriesItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct);


        // _log.LogInformation($"Gelato: done sync series");
        return seriesItem;
    }


}
