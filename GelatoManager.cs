using System.Diagnostics;
using Gelato.Config;
using Gelato.Decorators;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Gelato;

public sealed class GelatoManager(
    ILoggerFactory loggerFactory,
    IProviderManager provider,
    GelatoItemRepository repo,
    IFileSystem fileSystem,
    IMemoryCache memoryCache,
    IServerConfigurationManager serverConfig,
    ILibraryManager libraryManager,
    IDirectoryService directoryService
)
{
    public const string StreamTag = "gelato-stream";

    private readonly ILogger<GelatoManager> _log = loggerFactory.CreateLogger<GelatoManager>();

    private int GetHttpPort()
    {
        var networkConfig = serverConfig.GetNetworkConfiguration();
        return networkConfig.InternalHttpPort;
    }

    public void SetStremioSubtitlesCache(Guid guid, List<StremioSubtitle> subs)
    {
        memoryCache.Set($"subs:{guid}", subs, TimeSpan.FromMinutes(3600));
    }

    public List<StremioSubtitle>? GetStremioSubtitlesCache(Guid guid)
    {
        return memoryCache.Get<List<StremioSubtitle>>($"subs:{guid}");
    }

    public void SetStreamSync(string guid)
    {
        memoryCache.Set(
            $"streamsync:{guid}",
            guid,
            TimeSpan.FromSeconds(GelatoPlugin.Instance!.Configuration.StreamTTL)
        );
    }

    public bool HasStreamSync(string guid)
    {
        return memoryCache.TryGetValue($"streamsync:{guid}", out _);
    }

    public void SaveStremioMeta(Guid guid, StremioMeta meta)
    {
        memoryCache.Set($"meta:{guid}", meta, TimeSpan.FromMinutes(360));
    }

    public StremioMeta? GetStremioMeta(Guid guid)
    {
        return memoryCache.TryGetValue($"meta:{guid}", out var value) ? value as StremioMeta : null;
    }

    public void RemoveStremioMeta(Guid guid)
    {
        memoryCache.Remove($"meta:{guid}");
    }

    public void ClearCache()
    {
        if (memoryCache is MemoryCache cache)
        {
            cache.Compact(1.0);
        }

        _log.LogDebug("Cache cleared");
    }

    private static void SeedFolder(string path)
    {
        Directory.CreateDirectory(path);
        var seed = Path.Combine(path, "stub.txt");
        if (!File.Exists(seed))
        {
            File.WriteAllText(
                seed,
                "This is a seed file created by Gelato so that library scans are triggered. Do not remove."
            );
        }
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

    private Folder? TryGetFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        SeedFolder(path);
        return repo.GetItemList(new InternalItemsQuery { IsDeadPerson = true, Path = path })
            .OfType<Folder>()
            .FirstOrDefault();
    }

    private BaseItem? Exist(StremioMeta meta, User user)
    {
        var item = IntoBaseItem(meta);
        if (item?.ProviderIds is { Count: > 0 })
            return FindExistingItem(item, user);
        _log.LogWarning("Gelato: Missing provider ids, skipping");
        return null;
    }

    public BaseItem? FindExistingItem(BaseItem item, User user)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [item.GetBaseItemKind()],
            HasAnyProviderId = item.ProviderIds,
            Recursive = true,
            ExcludeTags = [StreamTag],
            User = user,
            IsDeadPerson = true, // skip filter marker
        };

        return libraryManager
            .GetItemList(query)
            .FirstOrDefault(x =>
            {
                return x switch
                {
                    null => false,
                    Video v => !v.IsStream(),
                    _ => true,
                };
            });
    }

    /// <summary>
    /// Inserts metadata into the library. Skip if it already exists.
    /// </summary>
    public async Task<(BaseItem? Item, bool Created)> InsertMeta(
        Folder parent,
        StremioMeta meta,
        User? user,
        bool allowRemoteRefresh,
        bool refreshItem,
        bool queueRefreshItem,
        CancellationToken ct
    )
    {
        var mediaType = meta.Type;
        BaseItem? existing;

        if (mediaType is not (StremioMediaType.Movie or StremioMediaType.Series))
        {
            _log.LogWarning("type {Type} is not valid, skipping", mediaType);
            return (null, false);
        }
        _log.LogDebug("inserting  {Name}", meta.Name);
        var baseItemKind = mediaType.ToBaseItem();
        var cfg = GelatoPlugin.Instance!.GetConfig(user?.Id ?? Guid.Empty);

        // load in full metadata if needed.
        if (
            allowRemoteRefresh
            && (
                meta.ImdbId is null
                || (
                    baseItemKind == BaseItemKind.Series
                    && (meta.Videos is null || meta.Videos.Count == 0)
                )
            )
        )
        {
            // do a prechexk as loading metadata is expensive
            existing = user is null ? null : Exist(meta, user);

            if (existing is not null)
            {
                _log.LogDebug(
                    "found existing {Kind}: {Id} for {Name}",
                    existing.GetBaseItemKind(),
                    existing.Id,
                    existing.Name
                );
                return (existing, false);
            }

            var lookupId = meta.ImdbId ?? meta.Id;
            meta = await cfg.Stremio!.GetMetaAsync(lookupId, mediaType).ConfigureAwait(false);

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

        existing = user is null ? null : Exist(meta, user);

        if (existing is not null)
        {
            _log.LogDebug(
                "found existing {Kind}: {Id} for {Name}",
                existing.GetBaseItemKind(),
                existing.Id,
                existing.Name
            );
            return (existing, false);
        }

        if (IntoBaseItem(meta) is not { } baseItem)
        {
            _log.LogWarning("failed to convert meta into base item for {Name}", meta.Name);
            return (null, false);
        }

        if (mediaType == StremioMediaType.Movie)
        {
            baseItem = SaveItem(baseItem, parent);
            if (baseItem is null)
            {
                _log.LogWarning("InsertMeta: failed to create baseItem");
                return (null, false);
            }

            await baseItem
                .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
                .ConfigureAwait(false);
        }
        else
        {
            baseItem = await SyncSeriesTreesAsync(cfg, meta, ct).ConfigureAwait(false);
        }

        if (baseItem is null)
        {
            _log.LogWarning("InsertMeta: failed to create {Type} for {Name}", mediaType, meta.Name);
            return (null, false);
        }

        if (refreshItem)
        {
            var options = new MetadataRefreshOptions(new DirectoryService(fileSystem))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllImages = false,
                ReplaceAllMetadata = false,
                ForceSave = true,
            };

            if (queueRefreshItem)
            {
                provider.QueueRefresh(baseItem.Id, options, RefreshPriority.High);
            }
            else
            {
                _ = provider.RefreshFullItem(baseItem, options, ct);
            }
        }
        _log.LogDebug("inserted new {Kind}: {Name}", baseItem.GetBaseItemKind(), baseItem.Name);
        return (baseItem, true);
    }

    private IEnumerable<BaseItem> FindByProviderIds(
        Dictionary<string, string> providerIds,
        BaseItemKind kind,
        Folder parent
    )
    {
        var q = new InternalItemsQuery
        {
            IncludeItemTypes = [kind],
            Recursive = true,
            ParentId = parent.Id,
            HasAnyProviderId = providerIds
                .Where(kvp =>
                    kvp.Key is nameof(MetadataProvider.Tmdb) or nameof(MetadataProvider.Tvdb)
                    || kvp.Key == nameof(MetadataProvider.TvRage)
                    || kvp.Key == "Stremio"
                    || kvp.Key == nameof(MetadataProvider.Imdb)
                )
                .ToDictionary(),
            GroupByPresentationUniqueKey = false,
            GroupBySeriesPresentationUniqueKey = false,
            CollapseBoxSetItems = false,
            // skip filter marker
            IsDeadPerson = true,
        };

        foreach (var item in libraryManager.GetItemList(q))
        {
            yield return item;
        }
    }

    private BaseItem? GetByProviderIds(
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
    public async Task<int> SyncStreams(BaseItem item, Guid userId, CancellationToken ct)
    {
        _log.LogDebug($"SyncStreams for {item.Id}");
        var stopwatch = Stopwatch.StartNew();
        if (item is not Video video)
        {
            _log.LogWarning(
                "SyncStreams: item is not a Video type, itemType={ItemType}",
                item.GetType().Name
            );
            return 0;
        }

        if (video.IsStream())
        {
            _log.LogWarning("SyncStreams: item is a stream, skipping");
            return 0;
        }

        var isEpisode = video is Episode;
        var parent = isEpisode ? video.GetParent() as Folder : TryGetMovieFolder(userId);
        if (parent is null)
        {
            _log.LogWarning("SyncStreams: no parent, skipping");
            return 0;
        }

        var uri = StremioUri.FromBaseItem(video);
        if (uri is null)
        {
            _log.LogError($"Unable to build Stremio URI for {video.Name}");
            return 0;
        }

        var providerIds = video.ProviderIds;
        providerIds.TryAdd("Stremio", uri.ExternalId);

        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        var stremio = cfg.Stremio;
        var streams = await stremio.GetStreamsAsync(uri).ConfigureAwait(false);
        var httpPort = GetHttpPort();

        // Filter valid streams
        var acceptable = streams
            .Select(s =>
            {
                if (!s.IsValid())
                {
                    _log.LogWarning("Invalid stream, skipping {StreamName}", s.Name);
                    return null;
                }

                if (!cfg.P2PEnabled && s.IsTorrent())
                {
                    _log.LogDebug($"P2P stream, skipping {s.Name}");
                    return null;
                }

                return s;
            })
            .Where(s => s is not null)
            .ToList();

        // Get existing streams
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [isEpisode ? BaseItemKind.Episode : BaseItemKind.Movie],
            HasAnyProviderId = providerIds,
            Recursive = true,
            IsDeadPerson = true,
            //  IsVirtualItem = true,
        };

        var existing = repo.GetItemList(query)
            .OfType<Video>()
            .Where(v => v.IsStream())
            .ToDictionary(v => v.GelatoData<Guid>("guid"));

        var newVideos = new List<Video>();

        for (var i = 0; i < acceptable.Count; i++)
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

            var id = s.GetGuid();
            var isNew = !existing.TryGetValue(id, out var target);

            if (isNew)
            {
                target =
                    isEpisode && video is Episode e
                        ? new Episode
                        {
                            //Id = libraryManager.GetNewItemId(path, typeof(Episode)),
                            SeriesId = e.SeriesId,
                            SeriesName = e.SeriesName,
                            SeasonId = e.SeasonId,
                            SeasonName = e.SeasonName,
                            IndexNumber = e.IndexNumber,
                            ParentIndexNumber = e.ParentIndexNumber,
                            PremiereDate = e.PremiereDate,
                        }
                        : new Movie
                        {
                            //Id = libraryManager.GetNewItemId(path, typeof(Movie))
                        };
                target.Path = path;
                target.Id = libraryManager.GetNewItemId(target.Path, target.GetType());
            }

            target.Name = video.Name;
            target.Tags = [StreamTag];

            var locked = target.LockedFields?.ToList() ?? [];
            if (!locked.Contains(MetadataField.Tags))
                locked.Add(MetadataField.Tags);
            target.LockedFields = locked.ToArray();

            target.ProviderIds = providerIds;
            target.RunTimeTicks = video.RunTimeTicks ?? video.RunTimeTicks;
            target.LinkedAlternateVersions = [];
            target.SetPrimaryVersionId(null);
            target.PremiereDate = video.PremiereDate;
            target.Path = path;
            target.IsVirtualItem = false;
            target.SetParent(parent);

            var users = target.GelatoData<List<Guid>>("userIds") ?? [];
            if (!users.Contains(userId))
            {
                users.Add(userId);
                target.SetGelatoData("userIds", users);
            }

            target.SetGelatoData("name", s.Name);
            target.SetGelatoData("description", s.Description);
            if (!string.IsNullOrEmpty(s.BehaviorHints?.BingeGroup))
            {
                target.SetGelatoData("bingeGroup", s.BehaviorHints.BingeGroup);
            }
            if (!string.IsNullOrEmpty(s.BehaviorHints?.Filename))
            {
                target.SetGelatoData("filename", s.BehaviorHints.Filename);
            }
            target.SetGelatoData("index", index);
            target.SetGelatoData("guid", target.Id);

            newVideos.Add(target);
        }

        //newVideos = SaveItems(newVideos, (Folder)primary.GetParent()).Cast<Video>().ToList();
        repo.SaveItems(newVideos, ct);

        var newIds = new HashSet<Guid>(newVideos.Select(x => x.Id));
        var stale = existing
            .Values.Where(m =>
                !newIds.Contains(m.Id)
                && (m.GelatoData<List<Guid>>("userIds")?.Contains(userId) ?? false)
            )
            .ToList();

        foreach (var _item in stale)
        {
            var users = _item.GelatoData<List<Guid>>("userIds") ?? [];
            users.Remove(userId);
            _item.SetGelatoData("userIds", users);
        }

        var toDelete = stale
            .Where(item => item.GelatoData<List<Guid>>("userIds") is { Count: 0 })
            .ToList();
        var toSave = stale.Except(toDelete).ToList();

        try
        {
            //repo.DeleteItem([.. toDelete.Select(f => f.Id)]);
        }
        catch
        {
            foreach (var staleItem in toDelete)
            {
                libraryManager.DeleteItem(
                    staleItem,
                    new DeleteOptions { DeleteFileLocation = true },
                    true
                );
            }
        }

        repo.SaveItems(toSave, ct);
        newVideos.Add(video);

        stopwatch.Stop();

        _log.LogInformation(
            $"SyncStreams finished GelatoId={uri.ExternalId} userId={userId} duration={Math.Round(stopwatch.Elapsed.TotalSeconds, 1)}s streams={newVideos.Count}"
        );

        return acceptable.Count;
    }

    /// <summary>
    /// We only check permissions cause jellyfin excludes remote items by default
    /// </summary>
    /// <param name="item"></param>
    /// <param name="user"></param>
    /// <returns></returns>
    public bool CanDelete(BaseItem item, User user)
    {
        var allCollectionFolders = libraryManager
            .GetUserRootFolder()
            .Children.OfType<Folder>()
            .ToList();

        return item.IsAuthorizedToDelete(user, allCollectionFolders);
    }

    public bool IsStremio(BaseItem item)
    {
        return item.IsGelato();
    }

    public async Task<BaseItem?> SyncSeriesTreesAsync(
        // Folder seriesRootFolder,
        PluginConfiguration cfg,
        StremioMeta seriesMeta,
        CancellationToken ct
    )
    {
        var seriesRootFolder = cfg.SeriesFolder;
        // Early validation
        if (seriesRootFolder is null || string.IsNullOrWhiteSpace(seriesRootFolder.Path))
        {
            _log.LogWarning("seriesRootFolder null or empty for {SeriesId}", seriesMeta.Id);
            return null;
        }
        var stopwatch = Stopwatch.StartNew();
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
        if (IntoBaseItem(seriesMeta) is not Series tmpSeries)
        {
            return null;
        }

        if (tmpSeries.ProviderIds.Count == 0)
        {
            _log.LogWarning(
                "No providers found for {SeriesId} {SeriesName}, skipping creation",
                seriesMeta.Id,
                seriesMeta.Name
            );
            return null;
        }

        if (
            GetByProviderIds(tmpSeries.ProviderIds, tmpSeries.GetBaseItemKind(), seriesRootFolder)
            is not Series series
        )
        {
            series = tmpSeries;
            if (series.Id == Guid.Empty)
                series.Id = Guid.NewGuid();

            var options = new MetadataRefreshOptions(directoryService)
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllImages = false,
                ReplaceAllMetadata = true,
                ForceSave = true,
            };

            series.ParentId = seriesRootFolder.Id;
            await series.RefreshMetadata(options, ct).ConfigureAwait(false);
            seriesRootFolder.AddChild(series);
            await series.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, ct);
        }

        var existingSeasonsDict = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    ParentId = series.Id,
                    IncludeItemTypes = [BaseItemKind.Season],
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

        var seasonsInserted = 0;
        var episodesInserted = 0;

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
                var epMeta = seasonGroup.First();
                epMeta.Type = StremioMediaType.Episode;
                if (IntoBaseItem(epMeta) is not Episode episode)
                {
                    _log.LogWarning(
                        "Could not load base item as episode for: {EpisodeName}, skipping",
                        epMeta.GetName()
                    );
                    continue;
                }

                season = new Season
                {
                    Id = libraryManager.GetNewItemId(seasonPath, typeof(Season)),
                    Name = $"Season {seasonIndex:D2}",
                    IndexNumber = seasonIndex,
                    SeriesId = series.Id,
                    SeriesName = series.Name,
                    Path = seasonPath,
                    DateLastRefreshed = DateTime.UtcNow,
                    SeriesPresentationUniqueKey = seriesPresentationKey,
                    DateModified = DateTime.UtcNow,
                    DateLastSaved = DateTime.UtcNow,
                    PremiereDate = episode.PremiereDate,
                    ParentId = series.Id,
                };

                var primary = seriesMeta.App_Extras?.SeasonPosters?[seasonIndex];
                if (!string.IsNullOrWhiteSpace(primary))
                {
                    season.ImageInfos = new List<ItemImageInfo>
                    {
                        new() { Type = ImageType.Primary, Path = primary },
                    }.ToArray();
                }

                season.SetProviderId("Stremio", $"{seriesStremioId}:{seasonIndex}");
                season.PresentationUniqueKey = season.CreatePresentationUniqueKey();
                series.AddChild(season);
                seasonsInserted++;
            }

            // Get existing episodes once per season and create dictionary
            var existingEpisodeNumbers = libraryManager
                .GetItemList(
                    new InternalItemsQuery
                    {
                        ParentId = season.Id,
                        IncludeItemTypes = [BaseItemKind.Episode],
                        Recursive = true,
                        IsDeadPerson = true,
                    }
                )
                .OfType<Episode>()
                .Where(x => !x.IsStream() && x.IndexNumber.HasValue)
                .Select(e => e.IndexNumber!.Value)
                .ToHashSet();
            var episodeList = new List<Episode>();
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
                    _log.LogTrace(
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
                if (IntoBaseItem(epMeta) is not Episode episode)
                {
                    _log.LogWarning(
                        "Could not load base item as episode for: {EpisodeName}, skipping",
                        epMeta.GetName()
                    );
                    continue;
                }

                episode.IndexNumber = index;
                episode.ParentIndexNumber = season.IndexNumber;
                episode.SeasonId = season.Id;
                episode.SeriesId = series.Id;
                episode.SeriesName = series.Name;
                episode.SeasonName = season.Name;
                episode.ParentId = season.Id;
                episode.SeriesPresentationUniqueKey = season.SeriesPresentationUniqueKey;
                episode.PresentationUniqueKey = episode.GetPresentationUniqueKey();

                episodeList.Add(episode);
                episodesInserted++;
                _log.LogTrace("Created episode {EpisodeName}", epMeta.GetName());
            }
            repo.SaveItems(episodeList, CancellationToken.None);
        }

        stopwatch.Stop();

        _log.LogInformation(
            "Sync completed for {SeriesName}: {SeasonsInserted} season(s) and {EpisodesInserted} episode(s) in {Dur}",
            series.Name,
            seasonsInserted,
            episodesInserted,
            stopwatch.Elapsed.TotalSeconds
        );

        return series;
    }

    public async Task SyncSeries(Guid userId, CancellationToken cancellationToken)
    {
        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        var seriesItems = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Series],
                    SeriesStatuses = [SeriesStatus.Continuing],
                    HasAnyProviderId = new Dictionary<string, string>
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

        var stremio = cfg.Stremio;

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
                await SyncSeriesTreesAsync(cfg, meta, cancellationToken);
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

    private BaseItem? SaveItem(BaseItem item, Folder parent)
    {
        return SaveItems([item], parent).FirstOrDefault();
    }

    private List<BaseItem> SaveItems(IEnumerable<BaseItem> items, Folder parent)
    {
        var baseItems = items.ToList();
        foreach (var item in baseItems)
        {
            var now = DateTime.UtcNow;
            item.DateModified = now;
            item.DateLastRefreshed = now;
            item.DateLastSaved = now;

            item.Id = libraryManager.GetNewItemId(item.Path, item.GetType());
            item.PresentationUniqueKey = item.CreatePresentationUniqueKey();

            parent.AddChild(item);
        }

        repo.SaveItems(baseItems, CancellationToken.None);
        return baseItems;
    }

    public BaseItem? IntoBaseItem(StremioMeta meta)
    {
        BaseItem item;

        var id = meta.Id;

        switch (meta.Type)
        {
            case StremioMediaType.Series:
                item = new Series { };
                break;

            case StremioMediaType.Movie:
                item = new Movie { };
                break;

            case StremioMediaType.Episode:
                item = new Episode { };
                break;
            default:
                _log.LogWarning("unsupported type {type}", meta.Type);
                return null;
        }

        item.Name = meta.GetName();
        item.PremiereDate = meta.GetPremiereDate();
        item.Path = $"gelato://stub/{id}";

        if (!string.IsNullOrWhiteSpace(meta.Runtime))
            item.RunTimeTicks = Utils.ParseToTicks(meta.Runtime);
        if (!string.IsNullOrWhiteSpace(meta.Description))
            item.Overview = meta.Description;

        // NOTICE: do this only for show and movie. cause the parent imdb is used for season abd episodes
        if (meta.Type is not StremioMediaType.Episode && !string.IsNullOrWhiteSpace(id))
        {
            var providerMappings = new (string Prefix, string Provider, bool StripPrefix)[]
            {
                ("tmdb:", nameof(MetadataProvider.Tmdb), true),
                ("tt", nameof(MetadataProvider.Imdb), false),
                ("anidb:", "AniDB", true),
                ("kitsu:", "Kitsu", true),
                ("mal:", "Mal", true),
                ("anilist:", "Anilist", true),
                ("tvdb:", nameof(MetadataProvider.Tvdb), true),
                ("tvmaze:", nameof(MetadataProvider.TvMaze), true),
            };

            foreach (var (prefix, prov, stripPrefix) in providerMappings)
            {
                if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var providerId = stripPrefix ? id[prefix.Length..] : id;
                item.SetProviderId(prov, providerId);
                break;
            }
        }

        if (!string.IsNullOrWhiteSpace(meta.ImdbId))
        {
            item.SetProviderId(MetadataProvider.Imdb, meta.ImdbId);
        }

        var stremioUri = new StremioUri(meta.Type, meta.ImdbId ?? id);
        item.SetProviderId("Stremio", stremioUri.ExternalId);
        item.IsVirtualItem = false;
        item.ProductionYear = meta.GetYear();

        item.Overview = meta.Description ?? meta.Overview;
        item.DateModified = DateTime.UtcNow;
        item.DateLastSaved = DateTime.UtcNow;

        var primary = meta.Poster ?? meta.Thumbnail;
        if (!string.IsNullOrWhiteSpace(primary))
        {
            item.ImageInfos = new List<ItemImageInfo>
            {
                new() { Type = ImageType.Primary, Path = primary },
            }.ToArray();
        }

        item.Id = libraryManager.GetNewItemId(item.Path, item.GetType());
        item.PresentationUniqueKey = item.CreatePresentationUniqueKey();
        return item;
    }
}
