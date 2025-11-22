#pragma warning disable SA1611
#pragma warning disable SA1591
#pragma warning disable SA1615
#pragma warning disable CS0165

using System.Collections.Generic;
using System.Globalization;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Gelato.Common;
using Gelato.Configuration;
using Gelato.Decorators;
using Jellyfin.Data.Enums;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Collections;
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
    //private readonly GelatoStremioProvider _stremioProvider;
    private readonly IServerConfigurationManager _config;
    private readonly IUserManager _user;
    private readonly ILibraryManager _library;
    private readonly GelatoItemRepository _repo;
    private readonly IDtoService _dtoService;
    private readonly IFileSystem _fileSystem;
    private readonly IProviderManager _provider;
    private IMemoryCache _memoryCache;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly IMediaStreamRepository _mediaStreams;
    private readonly ICollectionManager _collectionManager;
    private readonly GelatoStremioProviderFactory _stremioFactory;
    private readonly Folder? _seriesFolder;
    private readonly Folder? _movieFolder;

    public GelatoManager(
        ILoggerFactory loggerFactory,
        IProviderManager provider,
        GelatoStremioProviderFactory stremioFactory,
        //GelatoStremioProvider stremioProvider,
        IDtoService dtoService,
        IMediaStreamRepository mediaStreams,
        IServerConfigurationManager config,
        IUserManager userManager,
        GelatoItemRepository repo,
        IFileSystem fileSystem,
        IMemoryCache memoryCache,
        ICollectionManager collectionManager,
        IServerConfigurationManager serverConfig,
        ILibraryManager libraryManager
    )
    {
        _loggerFactory = loggerFactory;
        _memoryCache = memoryCache;
        _collectionManager = collectionManager;
        _log = loggerFactory.CreateLogger<GelatoManager>();
        _provider = provider;
        _mediaStreams = mediaStreams;
        _stremioFactory = stremioFactory;
        _dtoService = dtoService;
        _serverConfig = serverConfig;
        _config = config;
        _repo = repo;
        _user = userManager;
        _library = libraryManager;
        _fileSystem = fileSystem;

        _collectionManager.ItemsAddedToCollection += OnItemsAddedToCollection;
    }

    // jf preferes path but we want to match on id.
    private void OnItemsAddedToCollection(object sender, CollectionModifiedEventArgs e)
    {
        var collection = e.Collection;
        var addedItems = e.ItemsChanged;

        if (addedItems == null || addedItems.Count == 0)
            return;

        // Filter only Gelato items
        var gelatoItems = addedItems.Where(IsGelato).ToList();

        if (gelatoItems.Count == 0)
            return;

        var needsFix = false;

        for (int i = 0; i < collection.LinkedChildren.Length; i++)
        {
            var linkedChild = collection.LinkedChildren[i];

            // Check if this LinkedChild has a path but no LibraryItemId (newly added)
            if (
                string.IsNullOrEmpty(linkedChild.LibraryItemId)
                && !string.IsNullOrEmpty(linkedChild.Path)
            )
            {
                // Try to match it to one of the Gelato items by path
                var matchingItem = gelatoItems.FirstOrDefault(item =>
                    item.Path == linkedChild.Path
                );

                if (matchingItem != null)
                {
                    _log.LogDebug(
                        "Fixing Gelato LinkedChild with path {Path} for item {Id}",
                        linkedChild.Path,
                        matchingItem.Id
                    );

                    collection.LinkedChildren[i] = new LinkedChild
                    {
                        LibraryItemId = matchingItem.Id.ToString("N", CultureInfo.InvariantCulture),
                        Type = LinkedChildType.Manual,
                    };
                    needsFix = true;
                }
            }
        }

        if (needsFix)
        {
            _log.LogInformation(
                "Fixed {Count} Gelato items in collection {Name}",
                gelatoItems.Count,
                collection.Name
            );
            collection
                .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        }
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
        _memoryCache.Set(
            $"streamsync:{guid}",
            guid,
            TimeSpan.FromSeconds(GelatoPlugin.Instance!.Configuration.StreamTTL)
        );
    }

    public bool HasStreamSync(Guid guid)
    {
        return _memoryCache.TryGetValue($"streamsync:{guid}", out _);
    }

    public void SaveStremioMeta(Guid guid, StremioMeta meta)
    {
        _memoryCache.Set($"meta:{guid}", meta, TimeSpan.FromMinutes(360));
    }

    public StremioMeta? GetStremioMeta(Guid guid)
    {
        return _memoryCache.TryGetValue($"meta:{guid}", out var value)
            ? value as StremioMeta
            : null;
    }

    public void RemoveStremioMeta(Guid guid)
    {
        _memoryCache.Remove($"meta:{guid}");
    }

    public void ClearCache()
    {
        if (_memoryCache is MemoryCache cache)
        {
            cache.Compact(1.0);
        }

        _log.LogDebug("Cache cleared");
    }

    public static void SeedFolder(string path)
    {
        Directory.CreateDirectory(path);
        var seed = System.IO.Path.Combine(path, "stub.txt");
        if (!File.Exists(seed))
        {
            File.WriteAllText(
                seed,
                "This is a seed file created by Gelato so that library scans are triggered. Do not remove."
            );
        }
        var ignore = System.IO.Path.Combine(path, ".ignore");
        File.Delete(ignore);
    }

    public Folder? TryGetMovieFolder(Guid userId)
    {
        return TryGetFolder(
            GelatoPlugin.Instance!.Configuration.GetEffectiveConfig(userId).MoviePath
        );
    }

    public Folder? TryGetSeriesFolder(Guid userId)
    {
        return TryGetFolder(
            GelatoPlugin.Instance!.Configuration.GetEffectiveConfig(userId).SeriesPath
        );
    }

    public Folder? TryGetMovieFolder(PluginConfiguration cfg)
    {
        return TryGetFolder(cfg.MoviePath);
    }

    public Folder? TryGetSeriesFolder(PluginConfiguration cfg)
    {
        return TryGetFolder(cfg.SeriesPath);
    }

    public Folder? TryGetFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        SeedFolder(path);
        return _repo
            .GetItemList(new InternalItemsQuery { IsDeadPerson = true, Path = path })
            .OfType<Folder>()
            .FirstOrDefault();
    }

    private static bool IsValidUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    /// <summary>
    /// Inserts metadata into the library. Skip if it already exists.
    /// </summary>
    public async Task<(BaseItem? Item, bool Created)> InsertMeta(
        Folder parent,
        StremioMeta meta,
        Guid userId,
        bool allowRemoteRefresh,
        bool refreshItem,
        bool queueRefreshItem,
        CancellationToken ct
    )
    {
        var mediaType = meta.Type;

        if (mediaType is not (StremioMediaType.Movie or StremioMediaType.Series))
        {
            _log.LogWarning("type {Type} is not valid, skipping", mediaType);
            return (null, false);
        }

        var baseItemKind = mediaType.ToBaseItem();

        // load in full metadata if needed.
        if (
            allowRemoteRefresh
            && (
                meta.ImdbId is null
                || (
                    baseItemKind == BaseItemKind.Series
                    && (meta.Videos is null || !meta.Videos.Any())
                )
            )
        )
        {
            var lookupId = meta.ImdbId ?? meta.Id;
            var stremio = _stremioFactory.Create(userId);
            meta = await stremio.GetMetaAsync(lookupId, mediaType).ConfigureAwait(false);

            if (meta is null)
            {
                _log.LogWarning(
                    "InsertMeta: no aio meta found for {Id} {Type}, maybe try aiometadata as meta addon.",
                    lookupId,
                    mediaType
                );
                return (null, false);
            }

            mediaType = meta.Type;
            baseItemKind = mediaType.ToBaseItem();
        }

        if (!meta.IsValid())
        {
            _log.LogWarning(
                "meta for {Id} is not valid {Name} , skipping",
                meta.Id,
                meta.GetName()
            );
            return (null, false);
        }

        if (mediaType is not (StremioMediaType.Movie or StremioMediaType.Series))
        {
            _log.LogWarning("type {Type} is not valid after refresh, skipping", mediaType);
            return (null, false);
        }

        var baseItem = IntoBaseItem(meta);
        if (baseItem?.ProviderIds is not { Count: > 0 })
        {
            _log.LogWarning("Gelato: Missing provider ids, skipping");
            return (null, false);
        }

        var kind = baseItem.GetBaseItemKind();
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            HasAnyProviderId = baseItem.ProviderIds,
            Recursive = true,
            IsDeadPerson = true,
        };

        var existing = _library.GetItemList(query).OfType<BaseItem>().FirstOrDefault();
        //var existing = GetByProviderIds(baseItem.ProviderIds, kind, parent);
        if (existing is not null)
        {
            _log.LogDebug(
                "found existing {Kind}: {Id} for {Name}",
                existing.GetBaseItemKind(),
                existing.Id,
                baseItem.Name
            );
            return (existing, false);
        }

        if (mediaType == StremioMediaType.Movie)
        {
            //CreateStrmFile(baseItem.Path, baseItem.ShortcutPath);
            parent.AddChild(baseItem);
        }
        else
        {
            baseItem = await SyncSeriesTreesAsync(parent, meta, ct).ConfigureAwait(false);
        }

        if (baseItem is null)
        {
            _log.LogWarning("InsertMeta: failed to create {Type} for {Name}", mediaType, meta.Name);
            return (null, false);
        }

        _log.LogDebug("inserted new {Kind}: {Name}", baseItem.GetBaseItemKind(), baseItem.Name);

        if (queueRefreshItem || refreshItem)
        {
            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllImages = true,
                ReplaceAllMetadata = true,
                ForceSave = true,
            };

            if (queueRefreshItem)
            {
                _provider.QueueRefresh(baseItem.Id, options, RefreshPriority.High);
            }
            else
            {
                await _provider.RefreshFullItem(baseItem, options, ct).ConfigureAwait(false);
            }
        }

        return (baseItem, true);
    }

    public IEnumerable<BaseItem> FindByProviderIds(
        Dictionary<string, string> providerIds,
        BaseItemKind kind,
        Folder parent
    )
    {
        var q = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            Recursive = true,
            ParentId = parent.Id,
            HasAnyProviderId = providerIds
                .ExceptBy([MetadataProvider.TmdbCollection.ToString()], kvp => kvp.Key)
                .ToDictionary(),
            GroupByPresentationUniqueKey = false,
            GroupBySeriesPresentationUniqueKey = false,
            CollapseBoxSetItems = false,
            // skip filter marker
            IsDeadPerson = true,
        };

        return _library.GetItemList(q).OfType<BaseItem>();
    }

    public BaseItem? GetByProviderIds(
        Dictionary<string, string> providerIds,
        BaseItemKind kind,
        Folder parent
    )
    {
        return FindByProviderIds(providerIds, kind, parent).FirstOrDefault();
    }

    /// <summary>
    /// Load streams and inserts them into the database keeping original
    /// sorting. We make sure to keep a one stable version based on primaryversionid
    /// </summary>
    /// <returns></returns>
    public async Task SyncStreams(BaseItem item, Guid userId, CancellationToken ct)
    {
        _log.LogDebug($"SyncStreams for {item.Id}");

        if (item.IsVirtualItem)
        {
            _log.LogWarning($"SyncStreams: item is virtual, skipping");
            return;
        }

        var isEpisode = item is Episode;
        var parent = isEpisode ? item.GetParent() as Folder : TryGetMovieFolder(userId);
        if (parent is null)
        {
            _log.LogWarning($"SyncStreams: no parent, skipping");
            return;
        }

        var uri = StremioUri.FromBaseItem(item);
        if (uri is null)
        {
            _log.LogError($"Unable to build Stremio URI for {item.Name}");
            return;
        }

        var providerIds = item.ProviderIds ?? new Dictionary<string, string>();
        providerIds.TryAdd("Stremio", uri.ExternalId);
        var stremio = _stremioFactory.Create(userId);
        var streams = await stremio.GetStreamsAsync(uri).ConfigureAwait(false);
        var primary = (Video)item;
        var httpPort = GetHttpPort();
        var seriesFolder = TryGetSeriesFolder(userId);
        var movieFolder = TryGetMovieFolder(userId);

        // Get existing virtual items
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { isEpisode ? BaseItemKind.Episode : BaseItemKind.Movie },
            HasAnyProviderId = providerIds,
            Recursive = true,
            IsDeadPerson = true,
        };

        var existing = _repo
            .GetItemList(query)
            .OfType<Video>()
            .Where(v => IsStream(v))
            .ToDictionary(v => v.Id);

        // Filter valid streams
        var acceptable = streams
            .Select(s =>
            {
                if (!s.IsValid())
                {
                    _log.LogWarning("Invalid stream, skipping {StreamName}", s.Name);
                    return null;
                }

                if (!GelatoPlugin.Instance!.Configuration.P2PEnabled && s.IsTorrent())
                {
                    _log.LogDebug($"P2P stream, skipping {s.Name}");
                    return null;
                }

                return s;
            })
            .Where(s => s is not null)
            .ToList();

        // Handle case with no streams
        if (acceptable.Count == 0 && IsGelato(primary))
        {
            primary.Path = uri.ToString();
            await primary
                .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                .ConfigureAwait(false);
        }

        var newVideos = new List<Video>();

        // Process all streams - first one updates primary, rest create virtual items
        for (int i = 0; i < acceptable.Count; i++)
        {
            var s = acceptable[i];
            var index = i + 1;
            var path = s.IsFile()
                ? s.Url
                : $"http://127.0.0.1:{httpPort}/gelato/stream?ih={s.InfoHash}"
                    + (s.FileIdx is not null ? $"&idx={s.FileIdx}" : "")
                    + (
                        s.Sources is { Count: > 0 }
                            ? $"&trackers={Uri.EscapeDataString(string.Join(',', s.Sources))}"
                            : ""
                    );
            var externalId = $"{index:D3}:::{s.Name}";
            var id = s.GetGuid();
            var target = existing.GetValueOrDefault(id);
            var isNew = target is null;

            // First stream updates the primary item
            if (i == 0 && IsGelato(primary))
            {
                //primary.Path = GetGelatoLocalPath(primary);
                primary.Path = path;
                primary.ExternalId = externalId;
                //primary.IsShortcut = true;
                //primary.ShortcutPath = path;
                //CreateStrmFile(primary.Path, path);
                await primary
                    .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                    .ConfigureAwait(false);
                _mediaStreams.SaveMediaStreams(
                    primary.Id,
                    _mediaStreams.GetMediaStreams(new MediaStreamQuery { ItemId = id }),
                    ct
                );
                continue;
            }

            if (isNew)
            {
                target =
                    isEpisode && item is Episode e
                        ? new Episode
                        {
                            Id = id,
                            SeriesId = e.SeriesId,
                            SeriesName = e.SeriesName,
                            SeasonId = e.SeasonId,
                            SeasonName = e.SeasonName,
                            IndexNumber = e.IndexNumber,
                            ParentIndexNumber = e.ParentIndexNumber,
                            PremiereDate = e.PremiereDate,
                        }
                        : new Movie { Id = id };
            }

            target.ExternalId = externalId;
            target.Name = primary.Name;
            target.Path = path;
            //target.Path = GetGelatoLocalPath(target);
            //target.IsShortcut = true;
            //target.ShortcutPath = path;
            target.IsVirtualItem = true;
            target.ProviderIds = providerIds;
            target.RunTimeTicks = primary.RunTimeTicks ?? item.RunTimeTicks;
            target.Tags = primary.Tags;
            target.LinkedAlternateVersions = Array.Empty<LinkedChild>();
            target.SetPrimaryVersionId(null);

            //CreateStrmFile(target.Path, path);

            if (isNew)
            {
                parent.AddChild(target);
            }
            else
            {
                await target
                    .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                    .ConfigureAwait(false);
            }

            newVideos.Add(target);
        }

        var stale = existing.Values.Where(m => !newVideos.Any(x => x.Id == m.Id)).ToList();

        //foreach (var m in stale)
        //{
        //    File.Delete(m.Path);
        //}

        if (stale.Any())
        {
            _log.LogDebug($"SyncStreams: deleting {string.Join(", ", stale.Select(m => m.Id))}");
            _repo.DeleteItem(stale.Select(m => m.Id).ToList());
        }

        _log.LogInformation(
            $"SyncStreams finished for {item.Name}: {newVideos.Count} streams, {stale.Count} deleted"
        );
    }

    public static void CreateStrmFile(string path, string content)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var ignore = System.IO.Path.Combine(directory, ".ignore");
        if (!File.Exists(ignore))
            File.Create(ignore).Close();

        if (!path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            path += ".strm";

        File.WriteAllText(path, content.Trim(), Encoding.UTF8);
    }

    public bool IsPrimaryVersion(Video item)
    {
        return !item.IsVirtualItem;
        // return string.IsNullOrWhiteSpace(item.PrimaryVersionId);
    }

    public bool IsStream(Video item)
    {
        return IsGelato(item) && item.IsVirtualItem;
    }

    private static bool IsLocalFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
            return uri.IsFile;

        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return false;

        if (path.StartsWith("stremio", StringComparison.OrdinalIgnoreCase))
            return false;

        if (path.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
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

    public string GetGelatoLocalPath(BaseItem item, string? fileName = null)
    {
        var basePath =
            item.GetBaseItemKind() == BaseItemKind.Movie
                ? GelatoPlugin.Instance!.Configuration.MoviePath
                : GelatoPlugin.Instance!.Configuration.SeriesPath;

        if (item.GetBaseItemKind() == BaseItemKind.Episode)
        {
            var episode = (Episode)item;
            var series = episode.Series;

            if (series == null)
            {
                throw new InvalidOperationException("Episode must have a parent series");
            }

            // Series Name (Year)/Season 01/Series Name S01E01.strm
            var seriesFolder = series.ProductionYear.HasValue
                ? $"{series.Name} ({series.ProductionYear})"
                : series.Name;

            var seasonFolder = $"Season {episode.ParentIndexNumber:D2}";

            var episodeFileName =
                fileName
                ?? $"{series.Name} S{episode.ParentIndexNumber:D2}E{episode.IndexNumber:D2}";
            episodeFileName = Path.ChangeExtension(episodeFileName, ".strm");

            return Path.Combine(basePath, seriesFolder, seasonFolder, episodeFileName);
        }

        if (item.GetBaseItemKind() == BaseItemKind.Movie)
        {
            var movie = (Movie)item;

            // Movie Name (Year)/Movie Name (Year).strm
            var movieFolder = movie.ProductionYear.HasValue
                ? $"{movie.Name} ({movie.ProductionYear})"
                : movie.Name;

            var movieFileName = fileName ?? movieFolder;
            movieFileName = Path.ChangeExtension(movieFileName, ".strm");

            return Path.Combine(basePath, movieFolder, movieFileName);
        }

        // Fallback for other item types
        var fallbackFileName = fileName ?? item.Id.ToString("N");
        fallbackFileName = Path.ChangeExtension(fallbackFileName, ".strm");

        return Path.Combine(basePath, fallbackFileName);
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
                ["ids"] = new StringValues(value.ToString()),
            };

            ctx.HttpContext.Request.QueryString = QueryString.Create(dict);
        }
    }

    public async Task<BaseItem?> SyncSeriesTreesAsync(
        Folder seriesRootFolder,
        StremioMeta seriesMeta,
        CancellationToken ct
    )
    {
        // Early validation
        if (seriesRootFolder is null || string.IsNullOrWhiteSpace(seriesRootFolder.Path))
        {
            _log.LogWarning("seriesRootFolder null or empty for {SeriesId}", seriesMeta.Id);
            return null;
        }

        // Group episodes by season
        var seasonGroups = (seriesMeta.Videos ?? Enumerable.Empty<StremioMeta>())
            .Where(e => e.Season.HasValue && (e.Episode.HasValue || e.Number.HasValue)) // Filter out invalid episodes early
            .OrderBy(e => e.Season)
            .ThenBy(e => e.Episode ?? e.Number)
            .GroupBy(e => e.Season!.Value)
            .ToList();

        if (seasonGroups.Count == 0)
        {
            _log.LogWarning("No valid episodes found for {SeriesId}", seriesMeta.Id);
            return null;
        }

        // Create or get series
        var tmpSeries = (Series)IntoBaseItem(seriesMeta);

        if (tmpSeries.ProviderIds is null || tmpSeries.ProviderIds.Count == 0)
        {
            _log.LogWarning(
                "No providers found for {SeriesId} {SeriesName}, skipping creation",
                seriesMeta.Id,
                seriesMeta.Name
            );
            return null;
        }

        var series = (Series)GetByProviderIds(
            tmpSeries.ProviderIds,
            tmpSeries.GetBaseItemKind(),
            seriesRootFolder
        );
        if (series is null)
        {
            series = tmpSeries;
            if (series.Id == Guid.Empty)
                series.Id = Guid.NewGuid();
            series.PresentationUniqueKey = series.CreatePresentationUniqueKey();
            seriesRootFolder.AddChild(series);
        }

        var existingSeasonsDict = _library
            .GetItemList(
                new InternalItemsQuery
                {
                    ParentId = series.Id,
                    IncludeItemTypes = new[] { BaseItemKind.Season },
                    Recursive = true,
                    IsDeadPerson = true,
                }
            )
            .OfType<Season>()
            .Where(s => s.IndexNumber.HasValue)
            .GroupBy(s => s.IndexNumber!.Value)
            .Select(g =>
            {
                if (g.Count() > 1)
                {
                    _log.LogWarning(
                        "Duplicate seasons found for series {SeriesName} ({SeriesId})! Season {SeasonNum} exists {Count} times. IDs: {Ids}",
                        series.Name,
                        series.Id,
                        g.Key,
                        g.Count(),
                        string.Join(", ", g.Select(s => s.Id))
                    );
                }
                return g;
            })
            .ToDictionary(g => g.Key, g => g.First());

        int seasonsInserted = 0;
        int episodesInserted = 0;

        var seriesStremioId = series.GetProviderId("Stremio");
        var seriesPresentationKey = series.GetPresentationUniqueKey();

        foreach (var seasonGroup in seasonGroups)
        {
            ct.ThrowIfCancellationRequested();

            var seasonIndex = seasonGroup.Key;
            var seasonPath = $"{series.Path}:{seasonIndex}";

            if (!existingSeasonsDict.TryGetValue(seasonIndex, out var season))
            {
                _log.LogTrace(
                    "Creating series {SeriesName} season {SeasonIndex:D2}",
                    series.Name,
                    seasonIndex
                );

                season = new Season
                {
                    Id = Guid.NewGuid(),
                    Name = $"Season {seasonIndex:D2}",
                    IndexNumber = seasonIndex,
                    SeriesId = series.Id,
                    SeriesName = series.Name,
                    Path = seasonPath,
                    SeriesPresentationUniqueKey = seriesPresentationKey,
                };

                season.SetProviderId("Stremio", $"{seriesStremioId}:{seasonIndex}");
                series.AddChild(season);
                seasonsInserted++;

                _log.LogTrace("Created season with id {SeasonId}", season.Id);
            }

            // Get existing episodes once per season and create dictionary
            var existingEpisodeNumbers = _library
                .GetItemList(
                    new InternalItemsQuery
                    {
                        ParentId = season.Id,
                        IncludeItemTypes = new[] { BaseItemKind.Episode },
                        Recursive = true,
                        IsDeadPerson = true,
                    }
                )
                .OfType<Episode>()
                .Where(x => !IsStream(x) && x.IndexNumber.HasValue)
                .Select(e => e.IndexNumber!.Value)
                .ToHashSet();
            var seasonStremioId = season.GetProviderId("Stremio");

            foreach (var epMeta in seasonGroup)
            {
                ct.ThrowIfCancellationRequested();

                var index = epMeta.Episode ?? epMeta.Number;

                // This should never happen due to earlier filtering, but kept for safety
                if (!index.HasValue)
                {
                    _log.LogWarning(
                        "Episode number missing for: {EpisodeName}, skipping",
                        epMeta.GetName()
                    );
                    continue;
                }

                if (existingEpisodeNumbers.Contains(index.Value))
                {
                    _log.LogDebug(
                        "Episode {EpisodeName} already exists, skipping",
                        epMeta.GetName()
                    );
                    continue;
                }

                _log.LogTrace(
                    "Processing episode {EpisodeName} with index {Index} for {SeriesName} season {SeasonIndex}",
                    epMeta.GetName(),
                    index,
                    series.Name,
                    season.IndexNumber
                );

                epMeta.Type = StremioMediaType.Episode;
                var episode = (Episode)IntoBaseItem(epMeta);

                episode.IndexNumber = index;
                episode.ParentIndexNumber = season.IndexNumber;
                episode.SeasonId = season.Id;
                episode.SeriesId = series.Id;
                episode.SeriesName = series.Name;
                episode.SeasonName = season.Name;
                episode.SeriesPresentationUniqueKey = season.SeriesPresentationUniqueKey;
                episode.PresentationUniqueKey = episode.GetPresentationUniqueKey();
                episode.SetProviderId("Stremio", $"{seasonStremioId}:{index}");
                //episode.Path = GetGelatoLocalPath(episode);
                //episode.Path = GetGelatoLocalPath(episode);
                //CreateStrmFile(episode.Path, episode.ShortcutPath);
                season.AddChild(episode);
                episodesInserted++;

                _log.LogTrace("Created episode {EpisodeName}", epMeta.GetName());
            }
        }

        // Refresh metadata
        var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.None,
            ImageRefreshMode = MetadataRefreshMode.None,
            ReplaceAllImages = false,
            ReplaceAllMetadata = false,
            ForceSave = true,
        };

        await _provider.RefreshFullItem(series, options, CancellationToken.None);
        await series.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct);

        _log.LogInformation(
            "Sync completed for {SeriesName}: {SeasonsInserted} season(s) and {EpisodesInserted} episode(s) inserted",
            series.Name,
            seasonsInserted,
            episodesInserted
        );

        return series;
    }

    public async Task SyncSeries(
        bool runningOnly,
        Guid userId,
        IProgress<double> progress,
        CancellationToken cancellationToken
    )
    {
        var seriesFolder = TryGetSeriesFolder(userId);
        var seriesItems = _library
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Series },
                    SeriesStatuses = new[] { SeriesStatus.Continuing },
                    HasAnyProviderId = new()
                    {
                        { "Stremio", string.Empty },
                        { "stremio", string.Empty },
                    },
                }
            )
            .OfType<Series>()
            .ToList();

        _log.LogInformation(
            "found {Count} continuing series under TV libraries.",
            seriesItems.Count
        );
        var stremio = _stremioFactory.Create(userId);

        var processed = 0;
        foreach (var series in seriesItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _log.LogDebug(
                    "SyncSeries: syncing series trees for {Name} ({Id})",
                    series.Name,
                    series.Id
                );

                var meta = await stremio.GetMetaAsync(series).ConfigureAwait(false);
                if (meta is null)
                {
                    _log.LogWarning(
                        "SyncRunningSeries: skipping {Name} ({Id}) - no metadata found",
                        series.Name,
                        series.Id
                    );
                    continue;
                }
                await SyncSeriesTreesAsync(seriesFolder, meta, cancellationToken);
                processed++;
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex,
                    "SyncSeries: failed for {Name} ({Id}). Error: {ErrorMessage}",
                    series.Name,
                    series.Id,
                    ex.Message
                );
            }
        }

        _log.LogInformation(
            "SyncSeries completed. Processed {Processed}/{Total} series.",
            processed,
            seriesItems.Count
        );
    }

    public BaseItem IntoBaseItem(StremioMeta meta)
    {
        BaseItem item;

        var Id = meta.Id;

        switch (meta.Type)
        {
            case StremioMediaType.Series:
                item = new Series { Id = meta.Guid ?? _library.GetNewItemId(Id, typeof(Series)) };
                //item.Path = $"gelato://stub/{item.Id}";
                break;

            case StremioMediaType.Movie:
                item = new Movie { Id = meta.Guid ?? _library.GetNewItemId(Id, typeof(Movie)) };
                //item.Path = GetGelatoLocalPath(item);

                //item.IsShortcut = true;
                //item.ShortcutPath = $"gelato://stub/{item.Id}";
                break;

            case StremioMediaType.Episode:
                item = new Episode { Id = _library.GetNewItemId(Id, typeof(Episode)) };

                //item.IsShortcut = true;
                //item.ShortcutPath = $"gelato://stub/{item.Id}";
                break;
            default:
                _log.LogWarning("unsupported type {type}", meta.Type);
                return null;
        }
        ;

        item.Path = $"gelato://stub/{item.Id}";
        item.Name = meta.GetName();
        if (!string.IsNullOrWhiteSpace(meta.Runtime))
            item.RunTimeTicks = Utils.ParseToTicks(meta.Runtime);
        if (!string.IsNullOrWhiteSpace(meta.Description))
            item.Overview = meta.Description;

        // NOTICE: do this only for show and movie. cause the parent imdb is used for season abd episodes
        if (meta.Type is not StremioMediaType.Episode && !string.IsNullOrWhiteSpace(Id))
        {
            var providerMappings = new (string Prefix, string Provider, bool StripPrefix)[]
            {
                ("tmdb:", MetadataProvider.Tmdb.ToString(), true),
                ("tt", MetadataProvider.Imdb.ToString(), false),
                ("anidb:", "AniDB", true),
                ("kitsu:", "Kitsu", true),
                ("mal:", "Mal", true),
                ("anilist:", "Anilist", true),
                ("tvdb:", MetadataProvider.Tvdb.ToString(), true),
                ("tvmaze:", MetadataProvider.TvMaze.ToString(), true),
            };

            foreach (var (prefix, provider, stripPrefix) in providerMappings)
            {
                if (Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var providerId = stripPrefix ? Id.Substring(prefix.Length) : Id;
                    item.SetProviderId(provider, providerId);
                    break;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(meta.ImdbId))
        {
            item.SetProviderId(MetadataProvider.Imdb, meta.ImdbId);
        }

        var stremioUri = new StremioUri(meta.Type, meta.ImdbId ?? Id);
        item.SetProviderId("Stremio", stremioUri.ExternalId);

        // path is needed otherwise its set as placeholder and you cant play
        //item.Path = stremioUri.ToString();

        item.IsVirtualItem = false;
        item.ProductionYear = meta.GetYear();
        item.PremiereDate = meta.GetPremiereDate();
        // item.PresentationUniqueKey = item.CreatePresentationUniqueKey();
        item.Overview = meta.Description;

        if (!string.IsNullOrWhiteSpace(meta.Poster))
        {
            item.ImageInfos = new List<ItemImageInfo>
            {
                new ItemImageInfo { Type = ImageType.Primary, Path = meta.Poster },
            }.ToArray();
        }
        return item;
    }
}
