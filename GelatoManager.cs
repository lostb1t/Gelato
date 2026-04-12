using System.Diagnostics;
using System.Globalization;
using Gelato.Config;
using Gelato.Decorators;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
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
    IDirectoryService directoryService,
    IApplicationPaths appPaths
)
{
    public const string StreamTag = "gelato-stream";
    public const string TreeSyncedTag = "gelato-tree-synced";

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

        await EnrichMetaAsync(meta, ct).ConfigureAwait(false);

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

        var existingStreamItems = repo.GetItemList(query)
            .OfType<Video>()
            .Where(v => v.IsStream())
            .ToList();

        // Match stream rows by persisted Gelato guid, not by volatile playback URL/path.
        var existingByGuid = new Dictionary<Guid, Video>();
        foreach (var existingItem in existingStreamItems)
        {
            var existingGuid = existingItem.GelatoData<Guid?>("guid");
            if (existingGuid is null || existingGuid == Guid.Empty)
            {
                // Strict guid matching: ignore rows without a persisted guid.
                continue;
            }

            if (!existingByGuid.TryAdd(existingGuid.Value, existingItem))
            {
                // Guard against bad historical data; don't fail sync on collisions.
                _log.LogWarning(
                    "Duplicate stream guid found during sync: {Guid}. Keeping first item id={FirstId}, ignoring item id={SecondId}",
                    existingGuid.Value,
                    existingByGuid[existingGuid.Value].Id,
                    existingItem.Id
                );
            }
        }

        var upsertedStreams = new List<Video>();

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

            var streamGuid = s.GetGuid();
            var isNewStreamItem = !existingByGuid.TryGetValue(streamGuid, out var streamItem);

            if (isNewStreamItem)
            {
                streamItem =
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
                streamItem.Path = path;
                streamItem.Id = libraryManager.GetNewItemId(streamItem.Path, streamItem.GetType());
            }

            streamItem.Name = video.Name;
            streamItem.Tags = [StreamTag];

            var locked = streamItem.LockedFields?.ToList() ?? [];
            if (!locked.Contains(MetadataField.Tags))
                locked.Add(MetadataField.Tags);
            streamItem.LockedFields = locked.ToArray();

            streamItem.ProviderIds = providerIds;
            streamItem.RunTimeTicks = video.RunTimeTicks ?? video.RunTimeTicks;
            streamItem.LinkedAlternateVersions = [];
            streamItem.SetPrimaryVersionId(null);
            streamItem.PremiereDate = video.PremiereDate;
            streamItem.Path = path;
            streamItem.IsVirtualItem = false;
            streamItem.SetParent(parent);

            var users = streamItem.GelatoData<List<Guid>>("userIds") ?? [];
            if (!users.Contains(userId))
            {
                users.Add(userId);
                streamItem.SetGelatoData("userIds", users);
            }

            streamItem.SetGelatoData("name", s.Name);
            streamItem.SetGelatoData("description", s.Description);
            if (!string.IsNullOrEmpty(s.BehaviorHints?.BingeGroup))
            {
                streamItem.SetGelatoData("bingeGroup", s.BehaviorHints.BingeGroup);
            }
            if (!string.IsNullOrEmpty(s.BehaviorHints?.Filename))
            {
                streamItem.SetGelatoData("filename", s.BehaviorHints.Filename);
            }
            streamItem.SetGelatoData("index", index);
            streamItem.SetGelatoData("guid", streamGuid);
            // Keep map current so stale detection below uses the final upserted set.
            existingByGuid[streamGuid] = streamItem;

            upsertedStreams.Add(streamItem);
        }

        //upsertedStreams = SaveItems(upsertedStreams, (Folder)primary.GetParent()).Cast<Video>().ToList();
        repo.SaveItems(upsertedStreams, ct);

        var newIds = new HashSet<Guid>(upsertedStreams.Select(x => x.Id));
        var stale = existingByGuid
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
        upsertedStreams.Add(video);

        stopwatch.Stop();

        _log.LogInformation(
            $"SyncStreams finished GelatoId={uri.ExternalId} userId={userId} duration={Math.Round(stopwatch.Elapsed.TotalSeconds, 1)}s streams={upsertedStreams.Count}"
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
        PluginConfiguration cfg,
        StremioMeta seriesMeta,
        CancellationToken ct,
        Series? existingSeries = null
    )
    {
        var seriesRootFolder = cfg.SeriesFolder;

        Series series;

        if (existingSeries is not null)
        {
            // Local (non-gelato) series — use as-is, no creation needed
            series = existingSeries;
        }
        else
        {
            // Gelato series — create or find under the virtual folder
            if (seriesRootFolder is null || string.IsNullOrWhiteSpace(seriesRootFolder.Path))
            {
                _log.LogWarning("seriesRootFolder null or empty for {SeriesId}", seriesMeta.Id);
                return null;
            }

            if (IntoBaseItem(seriesMeta) is not Series tmpSeries)
                return null;

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
                GetByProviderIds(
                    tmpSeries.ProviderIds,
                    tmpSeries.GetBaseItemKind(),
                    seriesRootFolder
                )
                is not Series found
            )
            {
                tmpSeries.Id = tmpSeries.Id == Guid.Empty ? Guid.NewGuid() : tmpSeries.Id;

                var options = new MetadataRefreshOptions(directoryService)
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                    ReplaceAllImages = false,
                    ReplaceAllMetadata = true,
                    ForceSave = true,
                };

                tmpSeries.ParentId = seriesRootFolder.Id;
                await tmpSeries.RefreshMetadata(options, ct).ConfigureAwait(false);
                seriesRootFolder.AddChild(tmpSeries);
                await tmpSeries.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, ct);
                series = tmpSeries;
            }
            else
            {
                series = found;
            }
        }

        var stopwatch = Stopwatch.StartNew();

        // Group episodes by season
        var seasonGroups = (seriesMeta.Videos ?? Enumerable.Empty<StremioMeta>())
            .Where(e => e.Season.HasValue && (e.Episode.HasValue || e.Number.HasValue))
            .OrderBy(e => e.Season)
            .ThenBy(e => e.Episode ?? e.Number)
            .GroupBy(e => e.Season!.Value)
            .ToList();

        if (seasonGroups.Count == 0)
        {
            _log.LogWarning("No valid episodes found for {SeriesId}", seriesMeta.Id);
            return null;
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

        // Fetch all existing episodes for this series in one query, grouped by season
        var existingEpisodesBySeason = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    AncestorIds = [series.Id],
                    IncludeItemTypes = [BaseItemKind.Episode],
                    Recursive = true,
                    IsDeadPerson = true,
                }
            )
            .OfType<Episode>()
            .Where(x => !x.IsStream() && x.IndexNumber.HasValue && x.ParentIndexNumber.HasValue)
            .GroupBy(e => e.ParentIndexNumber!.Value)
            .ToDictionary(g => g.Key, g => g.Select(e => e.IndexNumber!.Value).ToHashSet());

        var seasonsInserted = 0;
        var episodesInserted = 0;

        var newSeasons = new List<Season>();
        var allNewEpisodes = new List<Episode>();

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
                    EndDate =
                        episode.PremiereDate ?? new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    ParentId = series.Id,
                };

                var primary = seriesMeta.App_Extras?.SeasonPosters?.ElementAtOrDefault(seasonIndex);
                if (!string.IsNullOrWhiteSpace(primary))
                {
                    ProviderManagerDecorator.SetRemoteImage(
                        appPaths,
                        season,
                        ImageType.Primary,
                        null,
                        primary
                    );
                }

                season.SetProviderId("Stremio", $"{seriesStremioId}:{seasonIndex}");
                season.PresentationUniqueKey = season.CreatePresentationUniqueKey();
                newSeasons.Add(season);
                seasonsInserted++;
            }

            // Look up existing episodes for this season from the pre-fetched dict
            var existingEpisodeNumbers = existingEpisodesBySeason.TryGetValue(seasonIndex, out var epNums)
                ? epNums
                : [];
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

                allNewEpisodes.Add(episode);
                episodesInserted++;
                _log.LogTrace("Created episode {EpisodeName}", epMeta.GetName());
            }
        }

        if (newSeasons.Count > 0)
            repo.SaveItems(newSeasons, ct);

        if (allNewEpisodes.Count > 0)
            repo.SaveItems(allNewEpisodes, ct);

        stopwatch.Stop();

        _log.LogDebug(
            "Sync completed for {SeriesName}: {SeasonsInserted} season(s) and {EpisodesInserted} episode(s) in {Dur}",
            series.Name,
            seasonsInserted,
            episodesInserted,
            stopwatch.Elapsed.TotalSeconds
        );

        return series;
    }

    /// <summary>
    /// Pass 1: fixes EndDate on all gelato media items (movies get TMDB digital release date,
    /// series/seasons/episodes get PremiereDate as EndDate).
    /// </summary>
    public async Task SyncReleaseDates(
        Guid userId,
        CancellationToken cancellationToken,
        IProgress<double>? progress = null
    )
    {
        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        var stremio = cfg.Stremio;
        if (stremio is null)
            return;

        var sentinel = new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        const int chunkSize = 400;
        const int maxDegreeOfParallelism = 4;

        var needsEndDate = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes =
                    [
                        BaseItemKind.Movie,
                        BaseItemKind.Series,
                        BaseItemKind.Season,
                        BaseItemKind.Episode,
                    ]
                }
            )
            .Where(m => m.EndDate is null || m.EndDate >= sentinel)
            .ToList();

        var total = needsEndDate.Count;
        var processed = 0;
        var totalSaved = 0;

        for (var chunkStart = 0; chunkStart < total; chunkStart += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = needsEndDate.Skip(chunkStart).Take(chunkSize).ToList();
            var chunkResults = new System.Collections.Concurrent.ConcurrentBag<BaseItem>();

            await Parallel
                .ForEachAsync(
                    chunk,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = maxDegreeOfParallelism,
                        CancellationToken = cancellationToken,
                    },
                    async (item, ct) =>
                    {
                        try
                        {
                            switch (item)
                            {
                                case Movie movie:
                                    {
                                        var meta = await stremio
                                            .GetMetaAsync(movie)
                                            .ConfigureAwait(false);
                                        if (meta is null)
                                            break;
                                        await EnrichMetaAsync(meta, ct).ConfigureAwait(false);
                                        var digital = meta.GetDigitalReleaseDate();
                                        movie.EndDate = digital ?? sentinel;
                                        chunkResults.Add(movie);
                                        _log.LogDebug(
                                            "SyncReleaseDates: movie {Name} EndDate → {Date}",
                                            movie.Name,
                                            movie.EndDate?.ToString(
                                                "yyyy-MM-dd",
                                                CultureInfo.InvariantCulture
                                            )
                                        );
                                        break;
                                    }

                                case BaseItem other when other is Series or Season or Episode:
                                    other.EndDate = other.PremiereDate ?? sentinel;
                                    chunkResults.Add(other);
                                    break;
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(
                                ex,
                                "SyncReleaseDates: failed for {Name} ({Id})",
                                item.Name,
                                item.Id
                            );
                        }
                        finally
                        {
                            var current = Interlocked.Increment(ref processed);
                            if (total > 0)
                                progress?.Report(100.0 * current / total);
                        }
                    }
                )
                .ConfigureAwait(false);

            if (!chunkResults.IsEmpty)
            {
                var toSave = chunkResults.ToList();
                repo.SaveItems(toSave, cancellationToken);
                totalSaved += toSave.Count;
            }
        }

        _log.LogInformation(
            "SyncReleaseDates completed. EndDate fixed for {Count} item(s).",
            totalSaved
        );
    }

    /// <summary>
    /// Syncs series trees: fetches new episodes for all continuing series (gelato + local),
    /// and extends local series trees for the first time if ExtendLocalSeriesTrees is enabled.
    /// </summary>
    public async Task SyncSeriesTrees(
        Guid userId,
        CancellationToken cancellationToken,
        IProgress<double>? progress = null
    )
    {
        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        var stremio = cfg.Stremio;
        if (stremio is null)
            return;

        var gelatoProviders = new Dictionary<string, string>
        {
            { "Stremio", string.Empty },
            { "stremio", string.Empty },
        };

        var continuingGelatoSeries = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Series],
                    SeriesStatuses = [SeriesStatus.Continuing],
                    HasAnyProviderId = gelatoProviders,
                }
            )
            .OfType<Series>()
            .ToList();

        var continuingLocalSeries = cfg.ExtendLocalSeriesTrees
            ? libraryManager
                .GetItemList(
                    new InternalItemsQuery
                    {
                        IncludeItemTypes = [BaseItemKind.Series],
                        SeriesStatuses = [SeriesStatus.Continuing],
                    }
                )
                .OfType<Series>()
                .Where(s =>
                    !s.IsGelato()
                    && (
                        !string.IsNullOrWhiteSpace(s.GetProviderId("Imdb"))
                        || !string.IsNullOrWhiteSpace(s.GetProviderId("Tmdb"))
                    )
                )
                .ToList()
            : [];

        var continuingSeries = continuingGelatoSeries.Concat(continuingLocalSeries).ToList();

        var total = continuingSeries.Count;
        var i = 0;

        await Parallel.ForEachAsync(
            continuingSeries,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = cancellationToken,
            },
            async (series, ct) =>
            {
                try
                {
                    var meta = await stremio.GetMetaAsync(series).ConfigureAwait(false);
                    if (meta is not null)
                    {
                        var isLocal = !series.IsGelato();
                        await SyncSeriesTreesAsync(
                                cfg,
                                meta,
                                ct,
                                existingSeries: isLocal ? series : null
                            )
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _log.LogError(
                        ex,
                        "SyncSeriesTrees: tree sync failed for {Name} ({Id})",
                        series.Name,
                        series.Id
                    );
                }
                finally
                {
                    if (total > 0)
                        progress?.Report(100.0 * Interlocked.Increment(ref i) / total);
                }
            }
        );

        _log.LogInformation(
            "SyncSeriesTrees: continuing series synced: {SeriesCount}.",
            continuingSeries.Count
        );

        if (cfg.ExtendLocalSeriesTrees)
        {
            await SyncLocalSeriesTreesAsync(cfg, stremio, cancellationToken, progress, i, total)
                .ConfigureAwait(false);
        }
        else
        {
            CleanVirtualTreeItems(cancellationToken);
        }
    }

    private async Task SyncLocalSeriesTreesAsync(
        PluginConfiguration cfg,
        GelatoStremioProvider stremio,
        CancellationToken ct,
        IProgress<double>? progress,
        int progressOffset,
        int progressTotal
    )
    {
        var localSeries = libraryManager
            .GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Series] })
            .OfType<Series>()
            .Where(s =>
                !s.IsGelato()
                && s.Status != SeriesStatus.Continuing // continuing handled in pass 2
                && (
                    !string.IsNullOrWhiteSpace(s.GetProviderId("Imdb"))
                    || !string.IsNullOrWhiteSpace(s.GetProviderId("Tmdb"))
                )
                && !(s.Tags?.Contains(TreeSyncedTag, StringComparer.OrdinalIgnoreCase) ?? false)
            )
            .ToList();

        _log.LogInformation(
            "SyncSeriesTrees: {Count} local (non-gelato, non-continuing) series to extend for the first time.",
            localSeries.Count
        );

        var total = progressTotal + localSeries.Count;
        var i = progressOffset;

        foreach (var series in localSeries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var meta = await stremio.GetMetaAsync(series).ConfigureAwait(false);
                if (meta is not null)
                {
                    await SyncSeriesTreesAsync(cfg, meta, ct, existingSeries: series)
                        .ConfigureAwait(false);

                    // Mark as synced so we skip on future runs
                    series.Tags = [.. (series.Tags ?? []), TreeSyncedTag];
                    repo.SaveItems([series], ct);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex,
                    "SyncSeriesTrees: virtual tree sync failed for {Name} ({Id})",
                    series.Name,
                    series.Id
                );
            }
            finally
            {
                if (total > 0)
                    progress?.Report(100.0 * ++i / total);
            }
        }
    }

    public void CleanVirtualTreeItem(Series series, CancellationToken ct)
    {
        var allEpisodes = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Episode],
                    AncestorIds = [series.Id],
                }
            )
            .OfType<Episode>()
            .ToList();

        var virtualEpisodes = allEpisodes.Where(ep => ep.IsGelato()).ToList();

        if (virtualEpisodes.Count == 0)
            return;

        var virtualEpIds = virtualEpisodes.Select(e => e.Id).ToHashSet();
        var seasonsWithRemainingEpisodes = allEpisodes
            .Where(ep => !virtualEpIds.Contains(ep.Id))
            .Select(ep => ep.SeasonId)
            .ToHashSet();

        _log.LogDebug(
            "CleanVirtualTreeItem: removing {EpCount} virtual episodes from {SeriesName}.",
            virtualEpisodes.Count,
            series.Name
        );

        foreach (var ep in virtualEpisodes)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                libraryManager.DeleteItem(ep, new DeleteOptions { DeleteFileLocation = false });
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex,
                    "CleanVirtualTreeItem: failed to delete episode {Name} ({Id})",
                    ep.Name,
                    ep.Id
                );
            }
        }

        var allSeasons = libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [BaseItemKind.Season],
                    ParentId = series.Id,
                }
            )
            .OfType<Season>()
            .ToList();

        foreach (var season in allSeasons)
        {
            ct.ThrowIfCancellationRequested();
            if (seasonsWithRemainingEpisodes.Contains(season.Id))
                continue;

            try
            {
                libraryManager.DeleteItem(season, new DeleteOptions { DeleteFileLocation = false });
                _log.LogDebug(
                    "CleanVirtualTreeItem: deleted empty season {Name} ({Id})",
                    season.Name,
                    season.Id
                );
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex,
                    "CleanVirtualTreeItem: failed to delete season {Name} ({Id})",
                    season.Name,
                    season.Id
                );
            }
        }

        series.Tags = series
            .Tags?.Where(t => !t.Equals(TreeSyncedTag, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        repo.SaveItems([series], ct);
    }

    private void CleanVirtualTreeItems(CancellationToken ct)
    {
        var localSeries = libraryManager
            .GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Series] })
            .OfType<Series>()
            .Where(s => string.IsNullOrEmpty(s.GetProviderId("Stremio")))
            .ToList();

        foreach (var series in localSeries)
        {
            ct.ThrowIfCancellationRequested();
            CleanVirtualTreeItem(series, ct);
        }
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

        // Always set EndDate so it's never NULL — NULL breaks MaxEndDate filtering (SQL NULL semantics).
        // Movies: use digital release date (TMDB type-4); sentinel 9999 means no digital date yet.
        // Series/Season/Episode: use premiere/firstAired date; sentinel 9999 means not yet known.
        {
            var sentinel = new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            item.EndDate =
                meta.Type == StremioMediaType.Movie
                    ? meta.GetDigitalReleaseDate() ?? sentinel
                    : meta.GetPremiereDate() ?? sentinel;
        }

        item.ProductionYear = meta.GetYear();
        item.Path = $"gelato://stub/{id}";

        // Provider IDs — skip for episodes since the parent series IMDB id is used there
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
            item.SetProviderId(MetadataProvider.Imdb, meta.ImdbId);

        var stremioUri = new StremioUri(meta.Type, meta.ImdbId ?? id);
        item.SetProviderId("Stremio", stremioUri.ExternalId);

        item.Overview = meta.Description ?? meta.Overview;

        if (meta.ImdbRating.HasValue)
            item.CommunityRating = meta.ImdbRating;

        if (!string.IsNullOrWhiteSpace(meta.Country))
            item.ProductionLocations =
            [
                CultureInfo.InvariantCulture.TextInfo.ToTitleCase(meta.Country.ToLowerInvariant()),
            ];

        if (!string.IsNullOrWhiteSpace(meta.App_Extras?.Certification))
            item.OfficialRating = meta.App_Extras.Certification;

        if (meta.Type is StremioMediaType.Movie or StremioMediaType.Series)
        {
            if (!string.IsNullOrWhiteSpace(meta.Runtime))
                item.RunTimeTicks = Utils.ParseToTicks(meta.Runtime);

            var genres = (meta.Genres ?? meta.Genre) ?? [];
            item.Genres = genres.Where(g => !string.IsNullOrWhiteSpace(g)).ToArray();
        }

        if (item is Episode ep)
        {
            ep.IndexNumber = meta.Episode ?? meta.Number;
            ep.ParentIndexNumber = meta.Season;
            if (!string.IsNullOrWhiteSpace(meta.Runtime))
                ep.RunTimeTicks = Utils.ParseToTicks(meta.Runtime);
            if (!string.IsNullOrWhiteSpace(meta.Thumbnail))
                ep.SetProviderId("StremioThumb", meta.Thumbnail);
            var tvdbId = meta.TvdbEpisodeId();
            if (tvdbId is not null)
                ep.SetProviderId(MetadataProvider.Tvdb, tvdbId);
        }

        if (item is Series series)
        {
            series.Status = meta.GetStatus() switch
            {
                StremioStatus.Continuing => SeriesStatus.Continuing,
                StremioStatus.Ended => SeriesStatus.Ended,
                StremioStatus.Upcoming => SeriesStatus.Unreleased,
                _ => null,
            };
        }

        item.IsVirtualItem = false;
        item.DateModified = DateTime.UtcNow;
        item.DateLastSaved = DateTime.UtcNow;
        item.DateCreated = DateTime.UtcNow;
        item.Id = libraryManager.GetNewItemId(item.Path, item.GetType());
        item.PresentationUniqueKey = item.CreatePresentationUniqueKey();

        var primaryImage = meta.Poster ?? meta.Thumbnail;
        if (!string.IsNullOrWhiteSpace(primaryImage))
            ProviderManagerDecorator.SetRemoteImage(
                appPaths,
                item,
                ImageType.Primary,
                null,
                primaryImage
            );

        return item;
    }

    /// <summary>
    /// Enriches <paramref name="meta"/> with digital release dates from TMDB when
    /// the meta is a movie and <c>App_Extras.ReleaseDates</c> is not yet populated.
    /// </summary>
    public async Task EnrichMetaAsync(StremioMeta meta, CancellationToken ct)
    {
        if (meta.Type != StremioMediaType.Movie)
            return;

        if (meta.App_Extras?.ReleaseDates is not null)
            return;

        var stremio = GelatoPlugin.Instance?.Configuration.Stremio;
        if (stremio is null)
            return;

        await stremio.EnrichDigitalReleaseDateAsync(meta, ct).ConfigureAwait(false);
    }
}
