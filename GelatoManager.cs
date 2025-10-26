#pragma warning disable SA1611
#pragma warning disable SA1591
#pragma warning disable SA1615
#pragma warning disable CS0165

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Gelato.Common;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Plugins;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
//using Jellyfin.Networking.Configuration;
//using Jellyfin.Server.Extensions;

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
    private IMemoryCache _memoryCache;
    private readonly IServerConfigurationManager _serverConfig;


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
          IServerConfigurationManager serverConfig,
        ILibraryManager libraryManager)
    {
        _loggerFactory = loggerFactory;
        _memoryCache = memoryCache;
        _log = loggerFactory.CreateLogger<GelatoManager>();
        _provider = provider;
        _stremioProvider = stremioProvider;
        _dtoService = dtoService;
        _serverConfig = serverConfig;
        _config = config;
        _repo = repo;
        _user = userManager;
        _library = libraryManager;
        _fileSystem = fileSystem;
    }

    public int GetHttpPort()
    {
        var networkConfig = _serverConfig.GetNetworkConfiguration();
        return networkConfig.InternalHttpPort;
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

    public void RemoveStremioMeta(Guid guid)
    {
        _memoryCache.Remove($"meta:{guid}");
    }


    public void ClearCache()
    {
        _memoryCache.Dispose();
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
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
        if (!meta.IsValid())
        {
            _log.LogError("meta is not valid, skipping");
            return (null, false);
        }

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

        var found = GetByProviderIds(baseItem.ProviderIds, baseItem.GetBaseItemKind());
        if (found is not null)
        {
            _log.LogDebug($"found existing item: {found.Id} for {baseItem.Name}");
            return (found, false);
        }

        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllImages = true,
            ReplaceAllMetadata = true,
            ForceSave = true
        };

        if (meta.Type == StremioMediaType.Movie)
        {
            parent.AddChild(baseItem);
        }

        if (meta.Type == StremioMediaType.Series)
        {
            baseItem = await SyncSeriesTreesAsync(parent, meta, ct);
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

    public BaseItem? GetByProviderIds(Dictionary<string, string> providerIds, BaseItemKind kind)
    {
        return FindByProviderIds(providerIds, kind).FirstOrDefault();
    }

    public BaseItem? GetExisting(StremioMeta meta)
    {
        var item = _stremioProvider.IntoBaseItem(meta);
        return GetByProviderIds(item.ProviderIds, item.GetBaseItemKind());
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

    /// <summary>
    /// Load streams and inserts them into the database keeping original
    /// sorting. We make sure to keep a one stable version based on primaryversionid
    /// </summary>
    /// <returns></returns>
    public async Task SyncStreams(BaseItem item, CancellationToken ct)
    {
        _log.LogDebug($"SyncStreams for {item.Id}");

        var isEpisode = item is Episode;
        var parent = isEpisode ? item.GetParent() as Folder : TryGetMovieFolder();
        if (parent is null) return;

        var providerIds = item.ProviderIds ?? new Dictionary<string, string>();
        var uri = StremioUri.FromBaseItem(item);
        if (uri is null)
        {
            _log.LogError($"Unable to build Stremio URI for {item.Name}");
            return;
        }

        var streams = await _stremioProvider.GetStreamsAsync(uri).ConfigureAwait(false);
        if (!providerIds.ContainsKey("Stremio")) providerIds["Stremio"] = uri.ExternalId;

        var query = new InternalItemsQuery
        {
            ParentId = parent.Id,
            IncludeItemTypes = new[] { isEpisode ? BaseItemKind.Episode : BaseItemKind.Movie },
            HasAnyProviderId = new() { { "Stremio", providerIds["Stremio"] } },
            Recursive = false,
            GroupByPresentationUniqueKey = false,
            GroupBySeriesPresentationUniqueKey = false,
            CollapseBoxSetItems = false
        };

        var items = _library.GetItemList(query)
            .OfType<Video>()
            .ToList();
        
        var localItems = items.Where(x => !IsGelato(x));
        var primary = items.FirstOrDefault(v => string.IsNullOrWhiteSpace(v.PrimaryVersionId));
        if (primary is null) {
          primary = (Video)item; 
        }
        
       // item = _libraryManager.GetItemById(item.Id);

        var current = items.Where(v => !v.IsFileProtocol && v.Id != primary.Id);
        var currentById = current.ToDictionary(v => v.Id, v => v);

        var keepIds = new HashSet<Guid>();
        if (primary is not null && IsGelato(primary)) keepIds.Add(primary.Id);

        var httpPort = GetHttpPort();

        var acceptable = new List<StremioStream>();
        foreach (var s in streams)
        {
            if (!s.IsValid())
            {
                _log.LogWarning($"Invalid stream, skipping {s.Name}");
                continue;
            }

            if (!GelatoPlugin.Instance!.Configuration.P2PEnabled && s.IsTorrent())
            {
                _log.LogDebug($"P2P stream, skipping {s.Name}");
                continue;
            }

            acceptable.Add(s);
        }

        // Primary-first stable ordering (keeps original order otherwise)
        if (primary is not null && acceptable.Count > 1)
        {
            acceptable = acceptable
                .OrderByDescending(s => s.GetGuid() == primary.Id)
                .ToList();
        }

        int index = 0;
        var newVideos = localItems
    .Where(i => i.Id != primary.Id)
    .Select(i =>
    {
        i.SetPrimaryVersionId(primary.Id.ToString("N", CultureInfo.InvariantCulture));
        return i;
    }).ToList();
  
        // Process in the order above — primary-related first
        foreach (var s in acceptable)
        {
            index++;
            var id = s.GetGuid();
          //  var label = $"{index:D3}:::{item.Name}:::{s.Name}";
            var path = s.IsFile()
                ? s.Url
                : $"http://127.0.0.1:{httpPort}/gelato/stream?ih={s.InfoHash}"
                    + (s.FileIdx is not null ? $"&idx={s.FileIdx}" : "")
                    + (s.Sources is { Count: > 0 } ? $"&trackers={Uri.EscapeDataString(string.Join(',', s.Sources))}" : "");

            // Choose target: existing → primary (once) → new
            Video target;
            var isNew = false;

            if (index == 1 && IsGelato(primary)) {
              target = primary;
}
            else if (currentById.TryGetValue(id, out var existing))
            {
                target = existing;
            }

            else
            {
                if (isEpisode && item is Episode e)
                {
                    target = new Episode
                    {
                        Id = id,
                        SeriesId = e.SeriesId,
                        SeriesName = e.SeriesName,
                        SeasonId = e.SeasonId,
                        SeasonName = e.SeasonName,
                        IndexNumber = e.IndexNumber,
                        ParentIndexNumber = e.ParentIndexNumber,
                        PremiereDate = e.PremiereDate
                    };
                }
                else
                {
                    target = new Movie { Id = id };
                }
                isNew = true;
            }

            // we use externalid for sorting and special. ame
            target.ExternalId = $"{index:D3}:::{s.Name}";
            target.Name = primary.Name;
            target.Path = path;
            target.IsVirtualItem = false;
            target.ProviderIds = providerIds;
            target.RunTimeTicks = primary.RunTimeTicks ?? item.RunTimeTicks;
            target.Tags = primary.Tags;

            if (target.Id != primary.Id) {
              target.SetPrimaryVersionId(primary.Id.ToString("N", CultureInfo.InvariantCulture));
              
              // this is a trick until the pr for primaryversion is merged
              target.IsVirtualItem = true;
            }

            if (isNew) {
               parent.AddChild(target);
            } else {
              // primary is saved later on
              if (!IsPrimaryVersion(target)) {
               await target.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
              }
            }

            newVideos.Add(target);
        }

        // Delete stale (never delete original base item)
        var stale = current
            .Where(m => !newVideos
                .Any(x => IsGelato(x) && !IsPrimaryVersion(x) && item.Id != x.Id && x.Id == m.Id))
            .ToList();

        foreach (var m in stale)
        {
            _log.LogDebug($"Deleting stale {m.Name}");
            _repo.DeleteItem([m.Id]);
        }
        
        primary.LinkedAlternateVersions = newVideos
            .Where(i => i.Id != primary.Id )
            .Select(i => new LinkedChild { Path = i.Path, ItemId = i.Id })
            .ToArray();
        primary.SetPrimaryVersionId(null);

        if (streams.Count == 0 && IsGelato(primary)) {
          primary.Path = uri.ToString();
        }         
        await primary.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
        
        _log.LogInformation($"SyncStreams finished for {item.Name} count: {newVideos.Count} deleted: {stale.Count}");
    }

    public Video? GetPrimaryVersion(List<Video> items)
    {
        return items.FirstOrDefault(i => i.MediaSourceCount > 1 && string.IsNullOrEmpty(i.PrimaryVersionId));
    }

    public bool IsPrimaryVersion(Video item)
    {
        return string.IsNullOrWhiteSpace(item.PrimaryVersionId);
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

        _log.LogDebug($"selected {primaryVersion.Name} {primaryVersion.Id} as primary version");

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

    public bool IsStremio(BaseItem item)
    {
        return IsGelato(item);
    }
    
    public bool IsGelato(BaseItem item)
    {
        var stremioId = item.GetProviderId("Stremio");
        if (!string.IsNullOrWhiteSpace(stremioId) && !item.IsFileProtocol)
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

    public async Task<BaseItem?> SyncSeriesTreesAsync(
           Folder seriesRootFolder,
           StremioMeta seriesMeta,
           CancellationToken ct)
    {
        if (seriesRootFolder is null || string.IsNullOrWhiteSpace(seriesRootFolder.Path))
        {
            _log.LogWarning($"seriesRootFolder null or empty for {seriesMeta.Id}");
            return null;
        }

        //var providerIds = seriesMeta.GetProviderIds();
        //if (providerIds is null || providerIds.Count == 0){
        //     _log.LogWarning($"no providers found for {seriesMeta.Id} {seriesMeta.Name}, skipping creation");
        //    return null;
        //}
        var groups = (seriesMeta.Videos ?? Enumerable.Empty<StremioMeta>())
            .OrderBy(e => e.Season)
            .ThenBy(e => e.Episode)
            .GroupBy(e => e.Season)
            .ToList();
        if (groups.Count == 0)
        {
            _log.LogWarning($"no episodes found for {seriesMeta.Id}");
            return null;
        }

        //var seriesStremioUri = StremioUri.LoadFromString(stremioKey);
        var tmpSeries = (Series)_stremioProvider.IntoBaseItem(seriesMeta);

        if (tmpSeries.ProviderIds is null || tmpSeries.ProviderIds.Count == 0)
        {
            _log.LogWarning($"no providers found for {seriesMeta.Id} {seriesMeta.Name}, skipping creation");
            return null;
        }

        // if (tmpSeries.GetProviderId("Imdb") is null)
        // {
        //     _log.LogWarning($"no imdb ID found for {seriesMeta.Id} {seriesMeta.Name}, skipping creation");
        //     return null;
        // }

        var seriesItem = (Series)GetByProviderIds(tmpSeries.ProviderIds, tmpSeries.GetBaseItemKind());
        if (seriesItem is null)
        {
            seriesItem = tmpSeries;
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
                _log.LogInformation($"insert episode id: {epPath}");
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
                if (!string.IsNullOrWhiteSpace(epMeta.Runtime))
                    epItem.RunTimeTicks = Utils.ParseToTicks(epMeta.Runtime);
                epItem.PresentationUniqueKey = epItem.GetPresentationUniqueKey();
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
