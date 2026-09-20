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
using MediaBrowser.Controller.Persistence;
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
    IItemPersistenceService persistence,
    IFileSystem fileSystem,
    IMemoryCache memoryCache,
    IServerConfigurationManager serverConfig,
    ILibraryManager libraryManager,
    IDirectoryService directoryService,
    IApplicationPaths appPaths,
    IUserManager userManager,
    IUserDataManager userDataManager
)
{
    public const string StreamTag = "gelato-stream";
    public const string TreeSyncedTag = "gelato-tree-synced";

    /// <summary>Name of the file Gelato seeds into its library folders.</summary>
    public const string SeedFileName = "stub.txt";

    /// <summary>
    /// Content of the seed file. Unchanged since the file was introduced, so a stub written by
    /// any earlier Gelato build is recognised as ours.
    /// </summary>
    public const string SeedFileContent =
        "This is a seed file created by Gelato so that library scans are triggered. Do not remove.";

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
            DateTime.UtcNow,
            TimeSpan.FromSeconds(GelatoPlugin.Instance!.Configuration.StreamTTL)
        );
    }

    /// <summary>
    /// Whether the streams behind the key were synced within StreamTTL and the movie/episode was
    /// not reset since (<see cref="ResetStreamSync"/>).
    /// </summary>
    public bool HasStreamSync(string guid, Guid itemId)
    {
        if (!memoryCache.TryGetValue($"streamsync:{guid}", out DateTime syncedAt))
            return false;

        return !memoryCache.TryGetValue($"streamsync-reset:{itemId}", out DateTime resetAt)
            || syncedAt > resetAt;
    }

    /// <summary>
    /// Makes the next visit of the movie/episode sync its streams again, for every user: after
    /// Jellyfin's split versions cleared the rows' owner and the links, or a merge changed them.
    /// </summary>
    public void ResetStreamSync(Guid itemId)
    {
        memoryCache.Set(
            $"streamsync-reset:{itemId}",
            DateTime.UtcNow,
            TimeSpan.FromSeconds(GelatoPlugin.Instance!.Configuration.StreamTTL)
        );
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

    /// <summary>
    /// Remembers which library item a search result became. A client that opened the result keeps
    /// its id in the page URL, and asks for the page, images, seasons, episodes and playback with
    /// it long after the result's metadata was dropped from the cache.
    /// </summary>
    public void RememberInsertedId(Guid searchId, Guid itemId)
    {
        memoryCache.Set($"inserted:{searchId}", itemId, TimeSpan.FromHours(24));
    }

    /// <summary>
    /// The item a search result became, while it exists: once it is deleted the result is a
    /// search result again, and opening it inserts anew.
    /// </summary>
    public Guid? GetInsertedId(Guid searchId)
    {
        if (!memoryCache.TryGetValue($"inserted:{searchId}", out Guid itemId))
            return null;

        if (libraryManager.GetItemById(itemId) is not null)
            return itemId;

        memoryCache.Remove($"inserted:{searchId}");
        return null;
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
        var seed = Path.Combine(path, SeedFileName);
        if (!File.Exists(seed))
        {
            File.WriteAllText(seed, SeedFileContent);
        }
    }

    /// <summary>
    /// Returns true when <paramref name="filePath"/> is a seed file Gelato wrote: same name and
    /// same content. A "stub.txt" holding anything else belongs to someone else.
    /// </summary>
    public static bool IsSeedFile(string filePath)
    {
        if (
            !string.Equals(
                Path.GetFileName(filePath),
                SeedFileName,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return false;
        }

        try
        {
            var info = new FileInfo(filePath);
            // Never read a large file just because it shares the name.
            if (!info.Exists || info.Length > 1024)
                return false;

            return string.Equals(
                File.ReadAllText(filePath).Trim(),
                SeedFileContent,
                StringComparison.Ordinal
            );
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes the seed file from <paramref name="folder"/> if it is the one Gelato wrote and
    /// leaves any other "stub.txt" alone. Returns true when the folder holds no seed file
    /// afterwards.
    /// </summary>
    public static bool RemoveSeedFile(string folder, ILogger log)
    {
        var seed = Path.Combine(folder, SeedFileName);
        if (!File.Exists(seed))
            return true;

        if (!IsSeedFile(seed))
        {
            log.LogWarning(
                "Gelato: {Seed} is not the seed file Gelato wrote, leaving it in place; a Jellyfin 12 upgrade may prune Gelato items.",
                seed
            );
            return false;
        }

        File.Delete(seed);
        log.LogInformation(
            "Gelato: removed seed file {Seed} so a Jellyfin 12 upgrade cannot prune the library.",
            seed
        );
        return true;
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

    // GetConfig asks for the root folders on every request, so the lookup is memoized.
    // The window is deliberately short: libraries can be added, moved or removed at any
    // time, and the answer must not be pinned for the lifetime of the process.
    private static readonly TimeSpan FolderCacheTtl = TimeSpan.FromSeconds(10);

    private Folder? TryGetFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var key = $"rootfolder:{path}";
        if (memoryCache.TryGetValue(key, out Folder? cached))
        {
            return cached;
        }

        SeedFolder(path);
        var folder = repo.GetItemList(new InternalItemsQuery { IsDeadPerson = true, Path = path })
            .OfType<Folder>()
            .FirstOrDefault();

        // Misses are cached too, so a configured-but-not-yet-added library does not cost
        // a directory probe and a query on every request while it is being set up.
        memoryCache.Set(key, folder, FolderCacheTtl);
        return folder;
    }

    private BaseItem? Exist(StremioMeta meta, User? user = null)
    {
        var item = IntoBaseItem(meta);
        if (item?.ProviderIds is { Count: > 0 })
            return FindExistingItem(item, user);
        _log.LogWarning("Gelato: Missing provider ids, skipping");
        return null;
    }

    public BaseItem? FindExistingItem(BaseItem item, User? user = null)
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
            // do a precheck as loading metadata is expensive
            existing = Exist(meta, user);

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
            meta = await cfg.Stremio!.GetMetaAsync(meta).ConfigureAwait(false);

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

        existing = Exist(meta, user);

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
            baseItem = await SaveItemAsync(baseItem, parent, ct).ConfigureAwait(false);
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
    /// One writer per movie/episode for its stream rows: a sync and a deletion of the same item
    /// must not interleave. A sync that finishes after the item was deleted would save the rows
    /// again and, when it links them, the item itself.
    /// </summary>
    private readonly KeyLock _itemWrites = new();

    /// <summary>
    /// Runs <paramref name="action"/> as the only writer of the given movie/episode's stream rows,
    /// queued behind a running sync or deletion of the same item.
    /// </summary>
    public Task RunExclusiveAsync(
        Guid itemId,
        Func<CancellationToken, Task> action,
        CancellationToken ct
    ) => _itemWrites.RunQueuedAsync(itemId, action, ct);

    /// <summary>
    /// Load streams and inserts them into the database keeping original
    /// sorting. We make sure to keep a one stable version based on primaryversionid
    /// </summary>
    /// <returns></returns>
    public async Task<int> SyncStreams(BaseItem item, Guid userId, CancellationToken ct)
    {
        var count = 0;
        await RunExclusiveAsync(
                item.Id,
                async token =>
                    count = await SyncStreamsCore(item, userId, token).ConfigureAwait(false),
                ct
            )
            .ConfigureAwait(false);
        return count;
    }

    private async Task<int> SyncStreamsCore(BaseItem item, Guid userId, CancellationToken ct)
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

        // A version merged into another item: the streams belong to that item.
        if (video.PrimaryVersionId is not null)
        {
            _log.LogDebug("SyncStreams: {Id} is a version of another item, skipping", video.Id);
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

        var streamProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (providerId, value) in video.ProviderIds)
        {
            streamProviderIds[providerId] = value;
        }
        streamProviderIds["Stremio"] = uri.ExternalId;

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

        // Get existing streams. A movie's rows only by Stremio id: movies of a collection share
        // the TmdbCollection id, and the other movies' rows would be treated as stale below.
        // Episodes keep matching on all ids, which also finds rows synced under an older
        // Stremio id.
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [isEpisode ? BaseItemKind.Episode : BaseItemKind.Movie],
            HasAnyProviderId = isEpisode
                ? streamProviderIds
                : new Dictionary<string, string> { { "Stremio", uri.ExternalId } },
            Recursive = true,
            IsDeadPerson = true,
            // Rows are alternate versions, which Jellyfin leaves out of queries by default.
            IncludeOwnedItems = true,
            //  IsVirtualItem = true,
        };

        // A row is a version of one movie/episode. The same title can exist more than once, e.g. a
        // local movie next to Gelato's, or per-user folders: each keeps rows of its own. Rows synced
        // before they were linked, or whose movie is gone, are taken over.
        var existingStreamItems = repo.GetItemList(query)
            .OfType<Video>()
            .Where(v =>
                v.IsStream()
                && (
                    v.PrimaryVersionId is not { } owner
                    || owner == video.Id
                    || libraryManager.GetItemById(owner) is null
                )
            )
            .ToList();

        // Rows synced before they were versions (an older Gelato, or a split): their watch state
        // moves to the movie/episode once they are adopted below.
        var legacyRows = existingStreamItems
            .Where(v => v.PrimaryVersionId is null)
            .Select(v => v.Id)
            .ToHashSet();

        // Match stream rows by persisted Gelato guid, not by volatile playback URL/path.
        var existingByGuid = new Dictionary<Guid, Video>();
        var duplicates = new List<Video>();
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
                duplicates.Add(existingItem);
                _log.LogWarning(
                    "Duplicate stream guid found during sync: {Guid}. Keeping first item id={FirstId}, deleting item id={SecondId}",
                    existingGuid.Value,
                    existingByGuid[existingGuid.Value].Id,
                    existingItem.Id
                );
            }
        }

        var upsertedStreams = new List<Video>();
        var now = DateTime.UtcNow;

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
                // Per movie/episode: another item of the same title gets its own row for the stream.
                streamItem.Id = libraryManager.GetNewItemId(
                    $"{path}#{video.Id:N}",
                    streamItem.GetType()
                );
            }

            streamItem.Tags = [StreamTag];

            var locked = streamItem.LockedFields?.ToList() ?? [];
            if (!locked.Contains(MetadataField.Tags))
                locked.Add(MetadataField.Tags);
            // The container's title tag would replace the name on probe when a library has
            // embedded titles enabled.
            if (!locked.Contains(MetadataField.Name))
                locked.Add(MetadataField.Name);
            streamItem.LockedFields = locked.ToArray();

            streamItem.ProviderIds = streamProviderIds;
            streamItem.RunTimeTicks = video.RunTimeTicks ?? video.RunTimeTicks;
            streamItem.LinkedAlternateVersions = [];
            streamItem.SetPrimaryVersionId(video.Id);
            CopyVersionMetadata(video, streamItem);
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

            // Stamped like the items SaveItemsAsync writes: a row is never refreshed by Jellyfin.
            // A library scan gives every child without a refresh date its first metadata refresh,
            // which saves the row again (bringing back one a purge deleted meanwhile) and moves
            // parked user data with the movie's keys onto it.
            streamItem.DateLastRefreshed = now;
            streamItem.DateLastSaved = now;

            upsertedStreams.Add(streamItem);
        }

        //upsertedStreams = SaveItems(upsertedStreams, (Folder)primary.GetParent()).Cast<Video>().ToList();
        persistence.SaveItems(upsertedStreams, ct);

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
            .Concat(duplicates)
            .ToList();
        var toSave = stale.Except(toDelete).ToList();

        // Every kept row becomes a version of this movie/episode below, so it needs the owner set:
        // the version's page, images, watch state and collections are all resolved through it.
        // Rows this sync did not touch (other users' rows synced before they were linked, or ones
        // whose movie is gone) would otherwise be linked without one.
        var kept = existingByGuid.Values.Except(toDelete).ToList();
        foreach (var row in kept)
        {
            if (upsertedStreams.Contains(row))
                continue;

            var changed = false;
            if (row.PrimaryVersionId != video.Id)
            {
                row.SetPrimaryVersionId(video.Id);
                changed = true;
            }

            // Rows synced before they were stamped (see above) get the refresh date too.
            if (row.DateLastRefreshed == DateTime.MinValue)
            {
                row.DateLastRefreshed = now;
                changed = true;
            }

            if (changed && !toSave.Contains(row))
                toSave.Add(row);
        }

        persistence.SaveItems(toSave, ct);

        // Rows are loaded here straight from the database, and saved around LibraryManager: without
        // this, version pages and the source list keep getting the rows as they were cached before.
        foreach (var row in upsertedStreams.Concat(toSave))
        {
            libraryManager.RegisterItem(row);
        }

        // Every row some user still has is a version of the movie/episode. Unlinking the rest
        // before they are deleted keeps Jellyfin from saving the movie once per deleted row.
        LinkVersions(video, kept, ct);
        AdoptWatchState(video, kept.Where(r => legacyRows.Contains(r.Id)).ToList());
        DeleteStreamRows(video, toDelete, ct);

        upsertedStreams.Add(video);

        stopwatch.Stop();

        _log.LogInformation(
            $"SyncStreams finished GelatoId={uri.ExternalId} userId={userId} duration={Math.Round(stopwatch.Elapsed.TotalSeconds, 1)}s streams={upsertedStreams.Count}"
        );

        return acceptable.Count;
    }

    /// <summary>
    /// Copies what a version's own page shows from its movie/episode. Jellyfin 12 clients load a
    /// version as the page item when it is picked, and Jellyfin does the same for local alternate
    /// versions in <see cref="Video.UpdateToRepositoryAsync"/>. Images, people and tags come from the
    /// movie at request time instead (<see cref="Decorators.DtoServiceDecorator"/>,
    /// <see cref="Filters.ImageResourceFilter"/>).
    /// </summary>
    private static void CopyVersionMetadata(Video primary, Video row)
    {
        row.Name = primary.Name;
        row.OriginalTitle = primary.OriginalTitle;
        row.Overview = primary.Overview;
        row.Tagline = primary.Tagline;
        row.Genres = primary.Genres;
        row.Studios = primary.Studios;
        row.ProductionLocations = primary.ProductionLocations;
        // No images of their own: Gelato downloads images per item on first view, so a copy would
        // be fetched again for every row and go stale when the movie's change.
        row.ImageInfos = [];
        row.ProductionYear = primary.ProductionYear;
        row.PremiereDate = primary.PremiereDate;
        row.EndDate = primary.EndDate;
        row.CommunityRating = primary.CommunityRating;
        row.CriticRating = primary.CriticRating;
        row.OfficialRating = primary.OfficialRating;
        row.CustomRating = primary.CustomRating;
        row.HomePageUrl = primary.HomePageUrl;
        row.RemoteTrailers = primary.RemoteTrailers;

        if (primary is Episode episode && row is Episode rowEpisode)
        {
            rowEpisode.SeriesName = episode.SeriesName;
            rowEpisode.SeasonName = episode.SeasonName;
            rowEpisode.IndexNumber = episode.IndexNumber;
            rowEpisode.ParentIndexNumber = episode.ParentIndexNumber;
        }
    }

    /// <summary>
    /// Makes the given stream rows the linked alternate versions of their movie/episode, in the
    /// order the addon returned them.
    /// </summary>
    private void LinkVersions(Video video, IReadOnlyCollection<Video> rows, CancellationToken ct)
    {
        // The links go onto the instance Jellyfin serves from its cache. A sync can run on a copy a
        // list query loaded, and the cached instance would keep its old links.
        var primary = libraryManager.GetItemById(video.Id) as Video ?? video;

        var rowIds = rows.Select(r => r.Id).ToHashSet();
        var linked = rows.Where(r => r.GelatoData<List<Guid>>("userIds") is { Count: > 0 })
            .OrderBy(r => r.GelatoData<int?>("index") ?? int.MaxValue)
            .Select(r => new LinkedChild
            {
                ItemId = r.Id,
                Type = MediaBrowser.Controller.Entities.LinkedChildType.LinkedAlternateVersion,
            });

        // Keep versions merged in by hand next to the streams. Read from the database, which the
        // links of every instance end up in.
        var stored = libraryManager.GetLinkedAlternateVersions(primary).ToList();
        var others = stored
            .Where(v => !rowIds.Contains(v.Id) && !v.HasStreamTag())
            .Select(v => new LinkedChild
            {
                ItemId = v.Id,
                Type = MediaBrowser.Controller.Entities.LinkedChildType.LinkedAlternateVersion,
            });

        LinkedChild[] links = [.. others, .. linked];
        var ids = links.Select(l => l.ItemId).ToList();
        if (
            ids.SequenceEqual(primary.LinkedAlternateVersions.Select(l => l.ItemId))
            && stored.Select(v => (Guid?)v.Id).ToHashSet().SetEquals(ids)
        )
        {
            return;
        }

        primary.LinkedAlternateVersions = links;
        if (!ReferenceEquals(primary, video))
        {
            video.LinkedAlternateVersions = links;
        }

        // Straight to the database: UpdateToRepositoryAsync would also run the metadata savers,
        // which write .nfo files next to a local movie's media.
        persistence.SaveItems([primary], ct);
    }

    /// <summary>
    /// Restores the version links of a movie/episode whose rows carry it as owner but are not
    /// linked: a metadata refresh that loaded the item before a sync and saved it afterwards (the
    /// one queued by a series insert, for the episode opened right away) writes the item's stale,
    /// empty link list. Returns whether any link was restored.
    /// </summary>
    public bool RelinkOwnedRows(Video primary)
    {
        if (primary.GetProviderId("Stremio") is not { Length: > 0 } stremioId)
            return false;

        var owned = repo.GetItemList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = [primary.GetBaseItemKind()],
                    HasAnyProviderId = new Dictionary<string, string> { { "Stremio", stremioId } },
                    Tags = [StreamTag],
                    Recursive = true,
                    IsDeadPerson = true,
                    IncludeOwnedItems = true,
                }
            )
            .OfType<Video>()
            .Where(v => v.HasStreamTag() && v.PrimaryVersionId == primary.Id)
            .ToList();
        if (owned.Count == 0)
            return false;

        _log.LogDebug("Restoring {Count} version link(s) of {Id}", owned.Count, primary.Id);
        LinkVersions(primary, owned, CancellationToken.None);
        return true;
    }

    /// <summary>
    /// The stream rows of a movie/episode: the ones linked to it, and rows of its title that no item
    /// has linked yet.
    /// </summary>
    public List<Video> GetStreamRows(Video primary)
    {
        var rows = libraryManager
            .GetLinkedAlternateVersions(primary)
            .Where(v => v.HasStreamTag())
            .ToList();

        if (primary.GetProviderId("Stremio") is { Length: > 0 } stremioId)
        {
            var unlinked = repo.GetItemList(
                    new InternalItemsQuery
                    {
                        IncludeItemTypes = [primary.GetBaseItemKind()],
                        HasAnyProviderId = new Dictionary<string, string>
                        {
                            { "Stremio", stremioId },
                        },
                        Tags = [StreamTag],
                        Recursive = true,
                        IsDeadPerson = true,
                        IncludeOwnedItems = true,
                    }
                )
                .OfType<Video>()
                .Where(v => v.HasStreamTag() && v.PrimaryVersionId is null);
            rows.AddRange(unlinked.Where(v => rows.All(r => r.Id != v.Id)));
        }

        return rows;
    }

    /// <summary>
    /// Deletes stream rows. Their watch state is already on the movie/episode
    /// (StreamUserDataSync).
    /// </summary>
    public void DeleteStreamRows(
        Video primary,
        IReadOnlyCollection<Video> rows,
        CancellationToken ct
    )
    {
        if (rows.Count == 0)
            return;

        // Playlist and collection entries that name a row move to the movie/episode, as Jellyfin
        // does for a deleted version while its owner is set. Before unlinking, which clears it.
        RerouteLinks(rows, primary.Id);

        // Unlinked first: Jellyfin does not save the movie again for every row, and
        // StreamUserDataSync does not copy the cleared watch state to the movie.
        foreach (var row in rows)
        {
            row.SetPrimaryVersionId(null);
        }

        // Deleted items park their user data under their keys, which rows share with the movie.
        ForgetWatchState(rows, ct);

        foreach (var row in rows)
        {
            try
            {
                libraryManager.DeleteItem(
                    row,
                    new DeleteOptions { DeleteFileLocation = false },
                    false
                );
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to delete stream {Id}", row.Id);
            }
        }

        _log.LogDebug("Deleted {Count} stream(s) of {Id}", rows.Count, primary.Id);
    }

    /// <summary>
    /// Makes legacy rows (no owner) versions of <paramref name="primary"/>: owner, refresh stamp,
    /// links next to the rows already linked, and the rows' watch state on the movie/episode.
    /// Run as the item's only writer (<see cref="RunExclusiveAsync"/>).
    /// </summary>
    public Task AdoptLegacyRows(
        Video primary,
        IReadOnlyCollection<Video> rows,
        CancellationToken ct
    )
    {
        var now = DateTime.UtcNow;
        foreach (var row in rows)
        {
            row.SetPrimaryVersionId(primary.Id);
            if (row.DateLastRefreshed == DateTime.MinValue)
            {
                row.DateLastRefreshed = now;
                row.DateLastSaved = now;
            }
        }

        persistence.SaveItems(rows.ToList<BaseItem>(), ct);
        foreach (var row in rows)
        {
            libraryManager.RegisterItem(row);
        }

        var rowIds = rows.Select(r => r.Id).ToHashSet();
        var all = libraryManager
            .GetLinkedAlternateVersions(primary)
            .Where(v => v.HasStreamTag() && !rowIds.Contains(v.Id))
            .Concat(rows)
            .ToList();
        LinkVersions(primary, all, ct);
        AdoptWatchState(primary, rows);
        _log.LogDebug("Adopted {Count} legacy stream row(s) of {Id}", rows.Count, primary.Id);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Playback through a row that was not a version yet saved its state on the row alone. When
    /// the rows become versions, the newest state among them moves to the movie/episode, per user,
    /// unless the movie's own is newer. Once: adopted rows have an owner from then on.
    /// </summary>
    private void AdoptWatchState(Video primary, IReadOnlyCollection<Video> rows)
    {
        if (rows.Count == 0)
            return;

        foreach (var user in userManager.GetUsers())
        {
            try
            {
                var best = rows.Select(r => userDataManager.GetUserData(user, r))
                    .Where(d => d is not null && (d.PlaybackPositionTicks > 0 || d.Played))
                    .OrderByDescending(d => d!.LastPlayedDate ?? DateTime.MinValue)
                    .FirstOrDefault();
                if (
                    best is null
                    || userDataManager.GetUserData(user, primary) is not { } data
                    || (data.LastPlayedDate ?? DateTime.MinValue)
                        >= (best.LastPlayedDate ?? DateTime.MinValue)
                )
                {
                    continue;
                }

                data.PlaybackPositionTicks = best.PlaybackPositionTicks;
                data.Played = best.Played || data.Played;
                data.PlayCount = Math.Max(data.PlayCount, best.PlayCount);
                data.LastPlayedDate = best.LastPlayedDate + Services.StreamUserDataSync.CopyOffset;
                userDataManager.SaveUserData(
                    user,
                    primary,
                    data,
                    UserDataSaveReason.UpdateUserData,
                    CancellationToken.None
                );
                _log.LogDebug(
                    "Adopted the watch state of a legacy stream row for {Name} on {Id}",
                    user.Username,
                    primary.Id
                );
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Could not adopt the watch state of {Id}'s rows", primary.Id);
            }
        }
    }

    /// <summary>
    /// Moves playlist and collection entries that name one of <paramref name="rows"/> to the
    /// movie/episode they are versions of (a version page adds the row it shows).
    /// </summary>
    public void RerouteLinks(IEnumerable<Video> rows, Guid primaryId)
    {
        foreach (var row in rows)
        {
            try
            {
                libraryManager
                    .RerouteLinkedChildReferencesAsync(row.Id, primaryId)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception ex)
            {
                _log.LogWarning(
                    ex,
                    "Could not move links of stream {Id} to {PrimaryId}",
                    row.Id,
                    primaryId
                );
            }
        }
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

    /// <summary>
    /// The path of a season Gelato adds to a series.
    /// </summary>
    /// <remarks>
    /// No folder exists on disk for it. Below a Gelato series the series path is a gelato:// URL
    /// and the season inherits it, but a local series sits on a real folder, so a season path
    /// built from it (the series folder with <c>:2</c> appended) is a file path as far as Jellyfin
    /// is concerned: a library scan deletes every file-backed child it does not find on disk, and
    /// takes the season's episodes and their watch state with it. A gelato:// path makes the
    /// season remote, which the scan leaves alone, the way it leaves the episodes below it alone.
    /// </remarks>
    private static string VirtualSeasonPath(Series series, int seasonIndex) =>
        !series.IsFileProtocol && !string.IsNullOrEmpty(series.Path)
            ? $"{series.Path}:{seasonIndex}"
            : $"gelato://season/{series.Id:N}:{seasonIndex}";

    /// <summary>
    /// Whether the series still carries the tree it was extended with: the mark the sync task
    /// leaves, and at least one of the seasons Gelato added.
    /// </summary>
    /// <remarks>
    /// The mark alone used to decide this, which made the skip permanent. Anything that removed
    /// the added seasons — a library scan on the paths of an older Gelato, a user deleting a
    /// season — left the mark behind, and from then on neither the task nor opening the series
    /// rebuilt the tree; only turning the option off and on did. Only the added seasons count: a
    /// series Gelato merely filled episodes into is looked at on every run, which costs one meta
    /// request and writes nothing.
    /// </remarks>
    public bool HasExtendedTree(Series series) =>
        (series.Tags?.Contains(TreeSyncedTag, StringComparer.OrdinalIgnoreCase) ?? false)
        && libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    ParentId = series.Id,
                    IncludeItemTypes = [BaseItemKind.Season],
                }
            )
            .Any(s => s.IsGelato());

    /// <summary>
    /// The item the tree sync would overwrite by creating one at <paramref name="path"/>, if there
    /// is one: every Gelato item takes its id from the hash of its path
    /// (<see cref="ILibraryManager.GetNewItemId"/>), so two items at the same path are one row.
    /// </summary>
    private T? ExistingItemAt<T>(string path)
        where T : BaseItem =>
        libraryManager.GetItemById(libraryManager.GetNewItemId(path, typeof(T))) as T;

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
                await ReattachWatchStateAsync([tmpSeries], ct).ConfigureAwait(false);
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
            .ToDictionary(
                g => g.Key,
                g =>
                    g.GroupBy(e => e.IndexNumber!.Value)
                        // A local episode and a Gelato one can share a number; update the Gelato one.
                        .ToDictionary(
                            n => n.Key,
                            n => n.FirstOrDefault(e => e.IsGelato()) ?? n.First()
                        )
            );

        var seasonsInserted = 0;
        var episodesInserted = 0;

        var newSeasons = new List<Season>();
        var repairedSeasons = new List<Season>();
        var allNewEpisodes = new List<Episode>();
        var updatedEpisodes = new List<Episode>();

        var seriesStremioId = series.GetProviderId("Stremio");
        var seriesPresentationKey = series.GetPresentationUniqueKey();

        foreach (var seasonGroup in seasonGroups)
        {
            ct.ThrowIfCancellationRequested();

            var seasonIndex = seasonGroup.Key;
            var seasonPath = VirtualSeasonPath(series, seasonIndex);

            if (
                !existingSeasonsDict.TryGetValue(seasonIndex, out var season)
                && ExistingItemAt<Season>(seasonPath) is { } collidingSeason
                && collidingSeason.SeriesId == series.Id
            )
            {
                // A season whose number someone cleared or changed is not in the dictionary, but
                // its row still lives at the path a new season for that number would get, and the
                // id is the path's hash: saving the new season would land on that row and replace
                // everything on it, the metadata lock included (lostb1t/Gelato#73). Keep the row
                // and only put the number back, which a locked season does not get either. Only
                // for a row of this series: a second copy of the same show (a local series beside
                // the addon's) shares those paths, and taking its items over is how extending a
                // local tree has always worked.
                season = collidingSeason;
                if (season.IsLocked)
                {
                    _log.LogDebug(
                        "Season {SeasonIndex:D2} of {SeriesName} is locked, leaving it as it is",
                        seasonIndex,
                        series.Name
                    );
                }
                else if (season.IndexNumber != seasonIndex)
                {
                    season.IndexNumber = seasonIndex;
                    repairedSeasons.Add(season);
                }
            }

            if (season is null)
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
            else if (season.IsGelato() && season.IsFileProtocol)
            {
                // Added by an older Gelato below a local series, so still carrying a path that
                // looks like a folder on disk. Repair it before the next scan takes the season
                // and its episodes with it; the id stays as it is, so nothing below moves.
                season.Path = seasonPath;
                repairedSeasons.Add(season);
            }

            // Look up existing episodes for this season from the pre-fetched dict
            var existingEpisodes = existingEpisodesBySeason.TryGetValue(seasonIndex, out var eps)
                ? eps
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

                if (existingEpisodes.TryGetValue(index.Value, out var existingEpisode))
                {
                    // Local episodes (no Stremio id) keep the metadata of their own library.
                    if (existingEpisode.IsGelato() && ApplyEpisodeMeta(existingEpisode, epMeta))
                    {
                        _log.LogTrace("Updated episode {EpisodeName}", existingEpisode.Name);
                        updatedEpisodes.Add(existingEpisode);
                    }
                    continue;
                }

                _log.LogTrace(
                    "Processing episode {EpisodeName} with index {Index} for {SeriesName} season {SeasonIndex}",
                    epMeta.GetName(),
                    index,
                    series.Name,
                    seasonIndex
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

                // Same as the season above: an episode of this series whose Season/Episode
                // someone cleared or changed is not in the lookup, but its row still lives at the
                // path the new episode gets and the id is that path's hash, so saving the new one
                // would replace it, lock and all (lostb1t/Gelato#73). Update that row from the
                // meta instead, which leaves a locked episode untouched.
                if (
                    ExistingItemAt<Episode>(episode.Path) is { } collidingEpisode
                    && collidingEpisode.SeriesId == series.Id
                )
                {
                    if (collidingEpisode.IsLocked)
                    {
                        _log.LogDebug(
                            "S{SeasonIndex:D2}E{Index:D2} of {SeriesName} is locked, leaving it as it is",
                            seasonIndex,
                            index,
                            series.Name
                        );
                    }
                    else if (ApplyEpisodeMeta(collidingEpisode, epMeta))
                    {
                        _log.LogTrace(
                            "Updated episode {EpisodeName} at {Path}",
                            collidingEpisode.Name,
                            collidingEpisode.Path
                        );
                        updatedEpisodes.Add(collidingEpisode);
                    }
                    continue;
                }

                episode.IndexNumber = index;
                episode.ParentIndexNumber = seasonIndex;
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
        {
            persistence.SaveItems(newSeasons, ct);
            await ReattachWatchStateAsync(newSeasons, ct).ConfigureAwait(false);
        }

        if (repairedSeasons.Count > 0)
        {
            persistence.SaveItems(repairedSeasons, ct);
            foreach (var season in repairedSeasons)
            {
                libraryManager.RegisterItem(season);
            }
        }

        if (allNewEpisodes.Count > 0)
        {
            persistence.SaveItems(allNewEpisodes, ct);
            await ReattachWatchStateAsync(allNewEpisodes, ct).ConfigureAwait(false);
        }

        if (updatedEpisodes.Count > 0)
        {
            // The episodes came fresh from the database, and the library manager caches only new
            // items, so register them: a cached copy would keep serving the placeholder, and
            // saving that copy later would write it back.
            foreach (var group in updatedEpisodes.GroupBy(e => e.ParentId))
            {
                await libraryManager
                    .UpdateItemsAsync(
                        group.ToList(),
                        group.First().GetParent() ?? series,
                        ItemUpdateType.MetadataImport,
                        ct
                    )
                    .ConfigureAwait(false);
            }

            foreach (var episode in updatedEpisodes)
            {
                libraryManager.RegisterItem(episode);
            }
        }

        stopwatch.Stop();

        _log.LogDebug(
            "Sync completed for {SeriesName}: {SeasonsInserted} season(s) and {EpisodesInserted} episode(s) inserted, {EpisodesUpdated} episode(s) updated in {Dur}",
            series.Name,
            seasonsInserted,
            episodesInserted,
            updatedEpisodes.Count,
            stopwatch.Elapsed.TotalSeconds
        );

        return series;
    }

    /// <summary>
    /// Brings an existing Gelato episode up to date with the addon's meta.
    /// </summary>
    /// <remarks>
    /// The tree sync used to skip every episode it had created before, so an episode added
    /// ahead of its release kept the addon's placeholder for good: "Episode 1", no overview, no
    /// runtime, the series backdrop as thumbnail. Only values the meta has are taken, and fields
    /// someone locked are left alone. The runtime is only filled in, since a probe may have
    /// measured a better one, and dates are compared by day, so a meta that carries a time of
    /// day does not rewrite every episode on every run.
    /// </remarks>
    /// <returns>Whether anything changed, i.e. whether the episode needs to be saved.</returns>
    private bool ApplyEpisodeMeta(Episode episode, StremioMeta meta)
    {
        if (episode.IsLocked)
            return false;

        var locked = episode.LockedFields ?? [];
        var changed = false;

        // Season and episode number: only relevant for an episode the caller found by its path
        // rather than by its number, i.e. one whose numbers were cleared or changed by hand. They
        // have no field of their own to lock, so the lock above is all there is to go by.
        if (meta.Season is { } season && season != episode.ParentIndexNumber)
        {
            episode.ParentIndexNumber = season;
            changed = true;
        }

        if ((meta.Episode ?? meta.Number) is { } number && number != episode.IndexNumber)
        {
            episode.IndexNumber = number;
            changed = true;
        }

        var name = meta.GetName();
        if (
            !string.IsNullOrWhiteSpace(name)
            && name != episode.Name
            && !locked.Contains(MetadataField.Name)
        )
        {
            episode.Name = name;
            changed = true;
        }

        var overview = string.IsNullOrWhiteSpace(meta.Description)
            ? meta.Overview
            : meta.Description;
        if (
            !string.IsNullOrWhiteSpace(overview)
            && overview != episode.Overview
            && !locked.Contains(MetadataField.Overview)
        )
        {
            episode.Overview = overview;
            changed = true;
        }

        if (
            episode.RunTimeTicks is null or 0
            && Utils.ParseToTicks(meta.Runtime) is { } runtime
            && !locked.Contains(MetadataField.Runtime)
        )
        {
            episode.RunTimeTicks = runtime;
            changed = true;
        }

        if (meta.GetPremiereDate() is { } premiere && premiere.Date != episode.PremiereDate?.Date)
        {
            episode.PremiereDate = premiere;
            episode.EndDate = premiere;
            episode.ProductionYear = premiere.Year;
            changed = true;
        }

        if (
            !string.IsNullOrWhiteSpace(meta.Thumbnail)
            && meta.Thumbnail != episode.GetProviderId("StremioThumb")
        )
        {
            try
            {
                ProviderManagerDecorator.SetRemoteImage(
                    appPaths,
                    episode,
                    ImageType.Primary,
                    null,
                    string.IsNullOrWhiteSpace(meta.Poster) ? meta.Thumbnail : meta.Poster
                );
                // Only recorded once the image is written, so a failed write is retried next run.
                episode.SetProviderId("StremioThumb", meta.Thumbnail);
                changed = true;
            }
            catch (IOException ex)
            {
                // Another sync of the same series is writing the same image; keep the rest.
                _log.LogDebug(ex, "Could not update the image of {EpisodeName}", episode.Name);
            }
        }

        if (
            meta.TvdbEpisodeId() is { } tvdbId
            && tvdbId != episode.GetProviderId(MetadataProvider.Tvdb)
        )
        {
            episode.SetProviderId(MetadataProvider.Tvdb, tvdbId);
            changed = true;
        }

        if (changed)
        {
            episode.DateModified = DateTime.UtcNow;
            episode.DateLastSaved = DateTime.UtcNow;
        }

        return changed;
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
        var now = DateTime.UtcNow;
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
                    ],
                    // Gelato's own items only: a native item has no EndDate, and writing one gives
                    // a running series an end date and stamps the 9999 sentinel on items whose
                    // premiere date the library does not know. Jellyfin's refresh only fills an
                    // empty EndDate, so those values would stay until a full metadata replace.
                    HasAnyProviderId = new Dictionary<string, string>
                    {
                        ["Stremio"] = string.Empty,
                    },
                }
            )
            // A locked item keeps the dates it has, like everywhere else the plugin writes
            // metadata (lostb1t/Gelato#73). A null EndDate only means the item is never taken for
            // unreleased, so leaving it is safe.
            .Where(m =>
                !m.IsLocked && (m.EndDate is null || m.EndDate >= sentinel || m.EndDate > now)
            )
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
                                        var oneYearAgo = DateTime.UtcNow.AddYears(-1);
                                        movie.EndDate =
                                            digital
                                            ?? (
                                                movie.PremiereDate.HasValue
                                                && movie.PremiereDate.Value < oneYearAgo
                                                    ? movie.PremiereDate.Value
                                                    : sentinel
                                            );
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
                persistence.SaveItems(toSave, cancellationToken);
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
                && !HasExtendedTree(s)
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

                    // Mark as synced so we skip on future runs. A series whose tree was rebuilt
                    // after it lost its seasons carries the mark already; adding it twice would
                    // show the tag twice on the item. The series came from the database, so the
                    // library manager's copy is the one from before the mark: register it, or
                    // the series page keeps extending the tree it already has, and the clean-up
                    // would later write the unmarked copy back.
                    if (
                        !(
                            series.Tags?.Contains(TreeSyncedTag, StringComparer.OrdinalIgnoreCase)
                            ?? false
                        )
                    )
                    {
                        series.Tags = [.. (series.Tags ?? []), TreeSyncedTag];
                        persistence.SaveItems([series], ct);
                        libraryManager.RegisterItem(series);
                    }
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

        // Only what Gelato itself put there. A file-backed episode belongs to the library that
        // scanned it, whatever provider ids it picked up along the way: Gelato's metadata
        // providers used to leave their Stremio id on local episodes, and removing those took the
        // show's own episodes out of the library — with the seasons they emptied — while the files
        // stayed on disk, so only a rescan of the library brought them back (lostb1t/Gelato#153).
        var virtualEpisodes = allEpisodes
            .Where(ep => ep.IsGelato() && !ep.IsFileProtocol)
            .ToList();

        if (virtualEpisodes.Count == 0)
        {
            // Nothing left to remove, but the mark has to go: it is what makes the sync task and
            // the series page skip the series, and a series without added episodes is one whose
            // tree is waiting to be rebuilt, not one that has it.
            ClearTreeSyncedTag(series, ct);
            return;
        }

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

            // A season of the local series itself stays, even with nothing left below it: the
            // library owns it, and the next scan would only have to find it again.
            if (!season.IsGelato())
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

        ClearTreeSyncedTag(series, ct);
    }

    /// <summary>Takes the sync task's mark off a series, if it carries one.</summary>
    private void ClearTreeSyncedTag(Series series, CancellationToken ct)
    {
        if (
            series.Tags is not { } tags
            || !tags.Contains(TreeSyncedTag, StringComparer.OrdinalIgnoreCase)
        )
            return;

        series.Tags =
        [
            .. tags.Where(t => !t.Equals(TreeSyncedTag, StringComparison.OrdinalIgnoreCase)),
        ];
        persistence.SaveItems([series], ct);
        libraryManager.RegisterItem(series);
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

    /// <summary>
    /// Clears the watch state of items that are about to be purged.
    /// </summary>
    /// <remarks>
    /// Only the purge uses this. Ordinary deletion leaves watch state alone, the way Jellyfin does
    /// for every item: the rows are parked rather than deleted, and come back if the item does. A
    /// purge is the one place where that is the wrong answer, because "remove all gelato items" is
    /// asking for a clean slate and the next catalog import would otherwise hand every play position
    /// straight back.
    ///
    /// The rows are zeroed rather than deleted, which needs no database access: SaveUserData writes
    /// one row per user data key, so what gets parked on deletion carries nothing.
    ///
    /// Cancellation is checked before each item. Items already cleared when the purge is cancelled
    /// stay cleared and undeleted; running the purge again finishes the job.
    /// </remarks>
    public void ForgetWatchState(IEnumerable<BaseItem> items, CancellationToken ct)
    {
        var users = userManager.GetUsers().ToList();
        if (users.Count == 0)
        {
            return;
        }

        var cleared = 0;
        var itemCount = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            itemCount++;

            foreach (var user in users)
            {
                try
                {
                    if (userDataManager.GetUserData(user, item) is not { } data || IsBlank(data))
                    {
                        continue;
                    }

                    data.Played = false;
                    data.PlayCount = 0;
                    data.PlaybackPositionTicks = 0;
                    data.IsFavorite = false;
                    data.LastPlayedDate = null;
                    data.Likes = null;
                    data.Rating = null;
                    data.AudioStreamIndex = null;
                    data.SubtitleStreamIndex = null;

                    userDataManager.SaveUserData(
                        user,
                        item,
                        data,
                        UserDataSaveReason.UpdateUserData,
                        ct
                    );
                    cleared++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Never let this block the deletion the user asked for.
                    _log.LogWarning(
                        ex,
                        "Could not clear watch state for {Name} ({Id})",
                        item.Name,
                        item.Id
                    );
                }
            }
        }

        if (cleared > 0)
        {
            // One row per item and user, so this is not an item count.
            _log.LogInformation(
                "Cleared {Rows} watch state row(s) for {Items} item(s) across {Users} user(s) being deleted",
                cleared,
                itemCount,
                users.Count
            );
        }
    }

    private static bool IsBlank(UserItemData data) =>
        !data.Played
        && data.PlayCount == 0
        && data.PlaybackPositionTicks == 0
        && !data.IsFavorite
        && data.LastPlayedDate is null
        && data.Likes is null
        && data.Rating is null;

    /// <summary>
    /// Reattaches watch state that Jellyfin parked on the detached-user-data placeholder the last
    /// time these items were removed.
    /// </summary>
    /// <remarks>
    /// Deleting an item does not delete its user data: Jellyfin moves the rows onto a placeholder
    /// item and stamps a retention date. It reattaches them again from
    /// <c>MetadataService.SaveItemAsync</c>, but only on an item's very first refresh
    /// (<c>DateLastRefreshed == DateTime.MinValue</c>). Gelato writes items straight through
    /// <see cref="IItemPersistenceService"/> with <c>DateLastRefreshed</c> already stamped, so that
    /// hook never fires for us — which is why a bulk removal (the Jellyfin 12 upgrade migration,
    /// <c>PurgeGelatoTask</c>) used to leave every resume position, played flag and favourite
    /// orphaned even after the catalogs were re-imported.
    ///
    /// Rows are matched on user data keys — the imdb/tmdb/tvdb ids, falling back to the item id —
    /// and Gelato item ids are a deterministic hash of path and type, so a re-imported item
    /// reproduces the exact keys it had before it was removed.
    /// </remarks>
    private async Task ReattachWatchStateAsync(IEnumerable<BaseItem> items, CancellationToken ct)
    {
        var reattached = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();

            // Stream rows copy the provider ids of the item they hang off, so they resolve to the
            // same user data keys. Reattaching onto one would move the watch state to a row the
            // user never sees.
            if (item.IsStream())
                continue;

            var before = item.UserData?.Count ?? 0;

            try
            {
                await persistence.ReattachUserDataAsync(item, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Jellyfin does not resolve key collisions here, so if the item already holds a row
                // for one of these keys the update violates the primary key. That row is newer than
                // anything on the placeholder, so leaving it untouched is the right outcome.
                _log.LogWarning(
                    ex,
                    "Could not reattach watch state for {Name} ({Id})",
                    item.Name,
                    item.Id
                );
                continue;
            }

            if ((item.UserData?.Count ?? 0) > before)
                reattached++;
        }

        if (reattached > 0)
            _log.LogDebug("Reattached watch state for {Count} item(s)", reattached);
    }

    private async Task<BaseItem?> SaveItemAsync(BaseItem item, Folder parent, CancellationToken ct)
    {
        return (await SaveItemsAsync([item], parent, ct).ConfigureAwait(false)).FirstOrDefault();
    }

    private async Task<List<BaseItem>> SaveItemsAsync(
        IEnumerable<BaseItem> items,
        Folder parent,
        CancellationToken ct
    )
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

        persistence.SaveItems(baseItems, CancellationToken.None);
        await ReattachWatchStateAsync(baseItems, ct).ConfigureAwait(false);
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
        // Exception: if PremiereDate is older than 1 year, use it as EndDate so old media without a
        // digital release date is never hidden by the unreleased filter.
        // Series/Season/Episode: use premiere/firstAired date; sentinel 9999 means not yet known.
        {
            var sentinel = new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var oneYearAgo = DateTime.UtcNow.AddYears(-1);
            item.EndDate =
                meta.Type == StremioMediaType.Movie
                    ? meta.GetDigitalReleaseDate()
                        ?? (
                            item.PremiereDate.HasValue && item.PremiereDate.Value < oneYearAgo
                                ? item.PremiereDate.Value
                                : sentinel
                        )
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

        // Fall back to the catalog id when ImdbId is absent OR blank. Addons
        // commonly emit "imdb_id": "" rather than omitting the field, and `??`
        // does not treat an empty string as missing - so a meta carrying a
        // perfectly good id (e.g. "tmdb:456173") reached StremioUri with "".
        var externalId = !string.IsNullOrWhiteSpace(meta.ImdbId) ? meta.ImdbId : id;

        // Every caller of IntoBaseItem already skips a null return, so honour
        // that contract instead of throwing: an exception here aborts the whole
        // request and takes every other result with it.
        if (string.IsNullOrWhiteSpace(externalId))
        {
            _log.LogWarning(
                "Skipping meta with no usable external id: {Name} ({Type})",
                meta.GetName(),
                meta.Type
            );
            return null;
        }

        var stremioUri = new StremioUri(meta.Type, externalId);
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
