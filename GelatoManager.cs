#pragma warning disable SA1611
#pragma warning disable SA1591
#pragma warning disable SA1615
#pragma warning disable CS0165

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Gelato.Common;
using Gelato.Decorators;
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
    private readonly GelatoItemRepository _repo;
    private readonly IDtoService _dtoService;
    private readonly IFileSystem _fileSystem;
    private readonly IProviderManager _provider;
    private IMemoryCache _memoryCache;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly IMediaStreamRepository _mediaStreams;

    public GelatoManager(
        ILoggerFactory loggerFactory,
        IProviderManager provider,
        GelatoStremioProvider stremioProvider,
        IDtoService dtoService,
        IMediaStreamRepository mediaStreams,
        IServerConfigurationManager config,
        IUserManager userManager,
        GelatoItemRepository repo,
        IFileSystem fileSystem,
        IMemoryCache memoryCache,
        IServerConfigurationManager serverConfig,
        ILibraryManager libraryManager
    )
    {
        _loggerFactory = loggerFactory;
        _memoryCache = memoryCache;
        _log = loggerFactory.CreateLogger<GelatoManager>();
        _provider = provider;
        _mediaStreams = mediaStreams;
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
            File.WriteAllText(
                seed,
                "This is a seed file created by Gelato so that library scans are triggered. Do not remove."
            );
    }

    public Folder? TryGetMovieFolder()
    {
        return TryGetFolder(GelatoPlugin.Instance!.Configuration.MoviePath);
    }

    public Folder? TryGetSeriesFolder()
    {
        return TryGetFolder(GelatoPlugin.Instance!.Configuration.SeriesPath);
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
    /// Inserts metadata into the library. Skip if exists
    /// </summary>
    /// <param name="meta"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public async Task<(BaseItem?, bool)> InsertMeta(
        Folder parent,
        StremioMeta meta,
        bool refreshItem,
        bool queueRefreshItem,
        CancellationToken ct
    )
    {
        if (!meta.IsValid())
        {
            _log.LogWarning("meta for {Name} is not valid, skipping", meta.Name);
            return (null, false);
        }

        if (meta.Type is not (StremioMediaType.Movie or StremioMediaType.Series))
        {
            _log.LogWarning("type {Type} is not valid, skipping", meta.Type);
            return (null, false);
        }

        var baseItem = _stremioProvider.IntoBaseItem(meta);
        if (baseItem?.ProviderIds is not { Count: > 0 })
        {
            _log.LogWarning("Gelato: Missing provider ids, skipping");
            return (null, false);
        }

        var found = GetByProviderIds(baseItem.ProviderIds, baseItem.GetBaseItemKind());
        if (found is not null)
        {
            _log.LogDebug(
                "found existing {Kind}: {Id} for {Name}",
                found.GetBaseItemKind(),
                found.Id,
                baseItem.Name
            );
            return (found, false);
        }

        if (meta.Type == StremioMediaType.Movie)
            parent.AddChild(baseItem);
        else
            baseItem = await SyncSeriesTreesAsync(parent, meta, ct);

        if (baseItem is not null)
        {
            _log.LogDebug("inserted new {Kind}: {Name}", baseItem.GetBaseItemKind(), baseItem.Name);

            var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllImages = true,
                ReplaceAllMetadata = true,
                ForceSave = true,
            };

            if (queueRefreshItem)
                _provider.QueueRefresh(baseItem.Id, options, RefreshPriority.High);
            else if (refreshItem)
                await _provider.RefreshFullItem(baseItem, options, ct);
        }

        return (baseItem, true);
    }

    public IEnumerable<BaseItem> FindByProviderIds(
        Dictionary<string, string> providerIds,
        BaseItemKind kind
    )
    {
        var q = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            Recursive = true,
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

    public BaseItem? GetByProviderIds(Dictionary<string, string> providerIds, BaseItemKind kind)
    {
        return FindByProviderIds(providerIds, kind).FirstOrDefault();
    }

    public BaseItem? GetExisting(StremioMeta meta)
    {
        var item = _stremioProvider.IntoBaseItem(meta);
        return GetByProviderIds(item.ProviderIds, item.GetBaseItemKind());
    }

    /// <summary>
    /// Load streams and inserts them into the database keeping original
    /// sorting. We make sure to keep a one stable version based on primaryversionid
    /// </summary>
    /// <returns></returns>
    public async Task SyncStreams(BaseItem item, CancellationToken ct)
    {
        _log.LogDebug($"SyncStreams for {item.Id}");

        if (item.IsVirtualItem)
        {
            _log.LogWarning($"SyncStreams: item is virtual, skipping");
            return;
        }

        var isEpisode = item is Episode;
        var parent = isEpisode ? item.GetParent() as Folder : TryGetMovieFolder();
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

        var streams = await _stremioProvider.GetStreamsAsync(uri).ConfigureAwait(false);
        var primary = (Video)item;
        var httpPort = GetHttpPort();

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
            .Where(v => !v.IsFileProtocol && v.Id != primary.Id)
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
                primary.Path = path;
                primary.ExternalId = externalId;
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
            target.IsVirtualItem = true;
            target.ProviderIds = providerIds;
            target.RunTimeTicks = primary.RunTimeTicks ?? item.RunTimeTicks;
            target.Tags = primary.Tags;
            target.LinkedAlternateVersions = Array.Empty<LinkedChild>();
            target.SetPrimaryVersionId(null);

            if (isNew)
                parent.AddChild(target);
            else
                await target
                    .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                    .ConfigureAwait(false);

            newVideos.Add(target);
        }

        // Delete stale items
        var stale = existing.Values.Where(m => !newVideos.Any(x => x.Id == m.Id)).ToList();

        _log.LogInformation(
            $"SyncStreams finished for {item.Name}: {newVideos.Count} streams, {stale.Count} deleted"
        );
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
            _log.LogWarning(
                $"no providers found for {seriesMeta.Id} {seriesMeta.Name}, skipping creation"
            );
            return null;
        }

        var series = (Series)GetByProviderIds(tmpSeries.ProviderIds, tmpSeries.GetBaseItemKind());
        if (series is null)
        {
            series = tmpSeries;
            if (series.Id == Guid.Empty)
                series.Id = Guid.NewGuid();
            series.PresentationUniqueKey = series.CreatePresentationUniqueKey();
            seriesRootFolder.AddChild(series);
        }

        var existingSeasons = _library
            .GetItemList(
                new InternalItemsQuery
                {
                    ParentId = series.Id,
                    IncludeItemTypes = new[] { BaseItemKind.Season },
                    Recursive = true,
                    IsDeadPerson = true,
                }
            )
            .OfType<Season>();

        foreach (var seasonGroup in groups)
        {
            ct.ThrowIfCancellationRequested();
            var seasonIndex = seasonGroup.Key;
            var seasonPath = $"{series.Path}:{seasonIndex}";

            var season = existingSeasons.Where(x => x.IndexNumber == seasonIndex).FirstOrDefault();
            if (season is null)
            {
                _log.LogDebug($"creating series {series.Name} season {seasonIndex:D2}");
                season = new Season
                {
                    Id = Guid.NewGuid(),
                    // IsVirtualItem = false,
                    Name = $"Season {seasonIndex:D2}",
                    IndexNumber = seasonIndex,
                    SeriesId = series.Id,
                    SeriesName = series.Name,
                    Path = seasonPath,
                    SeriesPresentationUniqueKey = series.GetPresentationUniqueKey(),
                };
                season.SetProviderId("Stremio", $"{series.GetProviderId("Stremio")}:{seasonIndex}");
                series.AddChild(season);
                await season
                    .RefreshMetadata(
                        new MetadataRefreshOptions(new DirectoryService(_fileSystem)),
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
                _log.LogDebug($"created season with id {season.Id}.");
                //  await seasonItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct);
            }

            var existingEpisodes = _library
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
                .Where(x => !IsStream(x))
                .ToDictionary(e => e.IndexNumber, e => e);

            var desiredIds = new HashSet<Guid>();

            foreach (var epMeta in seasonGroup)
            {
                ct.ThrowIfCancellationRequested();
                var index = epMeta.Episode ?? epMeta.Number;
                _log.LogDebug(
                    $"processing episode {epMeta.GetName()} with index {index} for {series.Name} season {season.IndexNumber}"
                );
                if (index is null)
                    _log.LogWarning($"episode number missing for: {epMeta.GetName()}, skipping");

                var epPath = $"{season.Path}:{index}";

                if (existingEpisodes.GetValueOrDefault(index) is not null)
                {
                    _log.LogDebug($"already exist, skipping episode");
                    continue;
                }

                var epItem = new Episode
                {
                    Id = Guid.NewGuid(),
                    Name = epMeta.GetName(),
                    IndexNumber = index,
                    ParentIndexNumber = epMeta.Season,
                    SeasonId = season.Id,
                    SeriesId = series.Id,
                    SeriesName = series.Name,
                    SeasonName = season.Name,
                    Path = epPath,
                    SeriesPresentationUniqueKey = season.SeriesPresentationUniqueKey,
                };
                if (!string.IsNullOrWhiteSpace(epMeta.Runtime))
                    epItem.RunTimeTicks = Utils.ParseToTicks(epMeta.Runtime);
                epItem.PresentationUniqueKey = epItem.GetPresentationUniqueKey();
                epItem.SetProviderId("Stremio", $"{season.GetProviderId("Stremio")}:{index}");
                season.AddChild(epItem);
                // await epItem.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct);
                _log.LogDebug($"created episode.");
            }
        }

        return series;
    }
}
