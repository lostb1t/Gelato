using System.Diagnostics;
using System.Globalization;
using System.Text;
using Gelato.Common;
using Gelato.Configuration;
using Gelato.Decorators;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Gelato;

public class GelatoManager {
    public const string StreamTag = "gelato-stream";

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
    private readonly IDirectoryService _directoryService;
    //private readonly Folder? _seriesFolder;
    //private readonly Folder? _movieFolder;

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
        ILibraryManager libraryManager,
        IDirectoryService directoryService
    ) {
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
        _directoryService = directoryService;

    }



    public int GetHttpPort() {
        var networkConfig = _serverConfig.GetNetworkConfiguration();
        return networkConfig.InternalHttpPort;
    }

    public void SetStremioSubtitlesCache(Guid guid, List<StremioSubtitle> subs) {
        _memoryCache.Set($"subs:{guid}", subs, TimeSpan.FromMinutes(3600));
    }

    public List<StremioSubtitle>? GetStremioSubtitlesCache(Guid guid) {
        return _memoryCache.Get<List<StremioSubtitle>>($"subs:{guid}");
    }

    public void SaveStremioUri(Guid guid, StremioUri stremioUri) {
        _memoryCache.Set($"uri:{guid}", stremioUri, TimeSpan.FromMinutes(3600));
    }

    public StremioUri? GetStremioUri(Guid guid) {
        return _memoryCache.TryGetValue($"uri:{guid}", out var value) ? value as StremioUri : null;
    }

    public void SetStreamSync(string guid) {
        _memoryCache.Set(
            $"streamsync:{guid}",
            guid,
            TimeSpan.FromSeconds(GelatoPlugin.Instance!.Configuration.StreamTTL)
        );
    }

    public bool HasStreamSync(string guid) {
        return _memoryCache.TryGetValue($"streamsync:{guid}", out _);
    }

    public void SaveStremioMeta(Guid guid, StremioMeta meta) {
        _memoryCache.Set($"meta:{guid}", meta, TimeSpan.FromMinutes(360));
    }

    public StremioMeta? GetStremioMeta(Guid guid) {
        return _memoryCache.TryGetValue($"meta:{guid}", out var value)
            ? value as StremioMeta
            : null;
    }

    public void RemoveStremioMeta(Guid guid) {
        _memoryCache.Remove($"meta:{guid}");
    }

    public void ClearCache() {
        if (_memoryCache is MemoryCache cache) {
            cache.Compact(1.0);
        }

        _log.LogDebug("Cache cleared");
    }

    public static void SeedFolder(string path) {
        // No-op: the FileSystemDecorator virtualises this directory from the DB.
    }

    public Folder? TryGetMovieFolder(Guid userId) {
        return TryGetFolder(
            GelatoPlugin.Instance!.Configuration.GetEffectiveConfig(userId).MoviePath
        );
    }

    public Folder? TryGetSeriesFolder(Guid userId) {
        return TryGetFolder(
            GelatoPlugin.Instance!.Configuration.GetEffectiveConfig(userId).SeriesPath
        );
    }

    public Folder? TryGetMovieFolder(PluginConfiguration cfg) {
        return TryGetFolder(cfg.MoviePath);
    }

    public Folder? TryGetSeriesFolder(PluginConfiguration cfg) {
        return TryGetFolder(cfg.SeriesPath);
    }

    public Folder? TryGetFolder(string path) {
        if (string.IsNullOrWhiteSpace(path)) {
            return null;
        }
        //path = $"gelato://stubfolder/{folder.Name}";
        SeedFolder(path);
        return _repo
            .GetItemList(new InternalItemsQuery { IsDeadPerson = true, Path = path })
            .OfType<Folder>()
            .FirstOrDefault();
        //folder.Path = $"gelato://stubfolder/{folder.Name}";
        //_repo.SaveItems([folder], CancellationToken.None);

        //return folder;
    }

    private static bool IsValidUrl(string? url) {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    public BaseItem? Exist(StremioMeta meta, Folder parent, User user) {
        var item = IntoBaseItem(meta);
        if (item?.ProviderIds is not { Count: > 0 }) {
            _log.LogWarning("Gelato: Missing provider ids, skipping");
            return null;
        }

        return FindExistingItem(item, user);
    }

    public BaseItem? FindExistingItem(BaseItem item, User user) {
        var query = new InternalItemsQuery {
            IncludeItemTypes = new[] { item.GetBaseItemKind() },
            HasAnyProviderId = item.ProviderIds,
            Recursive = true,
            ExcludeTags = new[] { StreamTag },
            User = user,
            IsDeadPerson = true, // skip filter marker
        };

        return _library
            .GetItemList(query)
            .FirstOrDefault(x => {
                if (x is null)
                    return false;

                if (x is Video v) {
                    return !IsStream(v);
                }

                return true;
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
        bool queueRefreshChildren,
        CancellationToken ct
    ) {
        var mediaType = meta.Type;
        BaseItem? existing;

        if (mediaType is not (StremioMediaType.Movie or StremioMediaType.Series)) {
            _log.LogWarning("type {Type} is not valid, skipping", mediaType);
            return (null, false);
        }
        _log.LogDebug("inserting  {Name}", meta.Name);
        var baseItemKind = mediaType.ToBaseItem();
        var cfg = GelatoPlugin.Instance!.GetConfig(user != null ? user.Id : Guid.Empty);

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
        ) {
            // do a prechexk as loading metadata is expensive
            existing = Exist(meta, parent, user);

            if (existing is not null) {
                _log.LogDebug(
                    "found existing {Kind}: {Id} for {Name}",
                    existing.GetBaseItemKind(),
                    existing.Id,
                    existing.Name
                );
                return (existing, false);
            }
            var lookupId = meta.ImdbId ?? meta.Id;
            meta = await cfg.stremio.GetMetaAsync(lookupId, mediaType).ConfigureAwait(false);

            if (meta is null) {
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

        if (!meta.IsValid()) {
            _log.LogWarning(
                "meta for {Id} is not valid {Name} , skipping",
                meta.Id,
                meta.GetName()
            );
            return (null, false);
        }

        if (mediaType is not (StremioMediaType.Movie or StremioMediaType.Series)) {
            _log.LogWarning("type {Type} is not valid after refresh, skipping", mediaType);
            return (null, false);
        }

        existing = Exist(meta, parent, user);

        if (existing is not null) {
            _log.LogDebug(
                "found existing {Kind}: {Id} for {Name}",
                existing.GetBaseItemKind(),
                existing.Id,
                existing.Name
            );
            return (existing, false);
        }

        var baseItem = IntoBaseItem(meta);

        if (mediaType == StremioMediaType.Movie) {
            baseItem = SaveItem(baseItem, parent);

            await baseItem
                .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
                .ConfigureAwait(false);
            
        }
        else {
            baseItem = await SyncSeriesTreesAsync(cfg, meta, ct).ConfigureAwait(false);
        }

        if (baseItem is null) {
            _log.LogWarning("InsertMeta: failed to create {Type} for {Name}", mediaType, meta.Name);
            return (null, false);
        }

        if (refreshItem) {
            var options = new MetadataRefreshOptions(_directoryService) {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllImages = false,
                ReplaceAllMetadata = false,
                ForceSave = true,
            };

            if (queueRefreshItem) {
                _provider.QueueRefresh(baseItem.Id, options, RefreshPriority.High);
            }
            else {
                if (baseItem is Series) {
                    // Queue with a FRESH DirectoryService to avoid stale cache from earlier lookups.
                    // By the time the queue processes this, all seasons/episodes will be in the DB.
                    var seriesOptions = new MetadataRefreshOptions(new DirectoryService(_fileSystem)) {
                        MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                        ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                        ReplaceAllImages = false,
                        ReplaceAllMetadata = false,
                        ForceSave = true,
                    };
                    _provider.QueueRefresh(baseItem.Id, seriesOptions, RefreshPriority.Normal);
                }
                else {
                    _provider.RefreshFullItem(baseItem, options, ct);
                }

            }
        }
        _log.LogDebug("inserted new {Kind}: {Name}", baseItem.GetBaseItemKind(), baseItem.Name);
        return (baseItem, true);
    }


    public IEnumerable<BaseItem> FindByProviderIds(
        Dictionary<string, string> providerIds,
        BaseItemKind kind,
        Folder parent
    ) {
        var q = new InternalItemsQuery {
            IncludeItemTypes = new[] { kind },
            Recursive = true,
            ParentId = parent.Id,
            HasAnyProviderId = providerIds
                .Where(kvp =>
                    kvp.Key == MetadataProvider.Tmdb.ToString()
                    || kvp.Key == MetadataProvider.Tvdb.ToString()
                    || kvp.Key == MetadataProvider.TvRage.ToString()
                    || kvp.Key == "Stremio"
                    || kvp.Key == MetadataProvider.Imdb.ToString()
                )
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
    ) {
        return FindByProviderIds(providerIds, kind, parent).FirstOrDefault();
    }

    /// <summary>
    /// Load streams and inserts them into the database keeping original
    /// sorting. We make sure to keep a one stable version based on primaryversionid
    /// </summary>
    /// <returns></returns>
    public async Task<int> SyncStreams(BaseItem item, Guid userId, CancellationToken ct) {
        _log.LogDebug($"SyncStreams for {item.Id}");
        var inv = CultureInfo.InvariantCulture;
        var stopwatch = Stopwatch.StartNew();
        if (IsStream(item as Video)) {
            _log.LogWarning($"SyncStreams: item is a stream, skipping");
            return 0;
        }

        var isEpisode = item is Episode;
        var parent = isEpisode ? item.GetParent() as Folder : TryGetMovieFolder(userId);
        if (parent is null) {
            _log.LogWarning($"SyncStreams: no parent, skipping");
            return 0;
        }

        var uri = StremioUri.FromBaseItem(item);
        if (uri is null) {
            _log.LogError($"Unable to build Stremio URI for {item.Name}");
            return 0;
        }

        var primary = item as Video;
        if (primary is null) {
            _log.LogError("SyncStreams: item is not a Video type, itemType={ItemType}", item?.GetType()?.Name);
            return 0;
        }

        var providerIds = primary.ProviderIds ?? new Dictionary<string, string>();
        providerIds.TryAdd("Stremio", uri.ExternalId);

        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        var stremio = cfg.stremio;
        var streams = await stremio.GetStreamsAsync(uri).ConfigureAwait(false);
        var httpPort = GetHttpPort();
        var seriesFolder = cfg.SeriesFolder;
        var movieFolder = cfg.MovieFolder;

        // Filter valid streams
        var acceptable = streams
            .Select(s => {
                if (!s.IsValid()) {
                    _log.LogWarning("Invalid stream, skipping {StreamName}", s.Name);
                    return null;
                }

                if (!cfg.P2PEnabled && s.IsTorrent()) {
                    _log.LogDebug($"P2P stream, skipping {s.Name}");
                    return null;
                }

                return s;
            })
            .Where(s => s is not null)
            .ToList();

        // Get existing streams
        var query = new InternalItemsQuery {
            IncludeItemTypes = new[] { isEpisode ? BaseItemKind.Episode : BaseItemKind.Movie },
            HasAnyProviderId = providerIds,
            Recursive = true,
            IsDeadPerson = true,
            //  IsVirtualItem = true,
        };

        var existing = _repo
            .GetItemList(query)
            .OfType<Video>()
            .Where(v => IsStream(v))
            .ToDictionary(v => v.GelatoData<Guid>("guid"));

        var newVideos = new List<Video>();

        for (int i = 0; i < acceptable.Count; i++) {
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
            //var id = Guid.NewGuid();
            // var target = existing.GetValueOrDefault(id);
            Video target;
            var isNew = !existing.TryGetValue(id, out target);

            if (isNew) {
                target =
                    isEpisode && item is Episode e
                        ? new Episode {
                            Id = _library.GetNewItemId(path, typeof(Episode)),
                            SeriesId = e.SeriesId,
                            SeriesName = e.SeriesName,
                            SeasonId = e.SeasonId,
                            SeasonName = e.SeasonName,
                            IndexNumber = e.IndexNumber,
                            ParentIndexNumber = e.ParentIndexNumber,
                            PremiereDate = e.PremiereDate,
                        }
                        : new Movie { Id = _library.GetNewItemId(path, typeof(Movie)) };
            }

            target.Name = primary.Name;
            target.PresentationUniqueKey = primary.PresentationUniqueKey;
            target.Tags = new[] { StreamTag };
            target.ProviderIds = providerIds;
            target.RunTimeTicks = primary.RunTimeTicks ?? item.RunTimeTicks;
            target.LinkedAlternateVersions = Array.Empty<LinkedChild>();
            // target.SetParent(parent);
            target.SetPrimaryVersionId(null);
            //target.SetPrimaryVersionId(primary.Id.ToString());
            target.PremiereDate = primary.PremiereDate;
            target.Path = path;

            var users = target.GelatoData<List<Guid>>("userIds") ?? new List<Guid>();
            if (!users.Contains(userId)) {
                users.Add(userId);
                target.SetGelatoData("userIds", users);
            }

            target.SetGelatoData("name", s.Name);
            target.SetGelatoData("description", s.Description);
            if (!string.IsNullOrEmpty(s.BehaviorHints?.BingeGroup)) {
                target.SetGelatoData("bingeGroup", s.BehaviorHints.BingeGroup);
            }
            if (!string.IsNullOrEmpty(s.BehaviorHints?.Filename)) {
                target.SetGelatoData("filename", s.BehaviorHints.Filename);
            }
            target.SetGelatoData("index", index);
            target.SetGelatoData("guid", id);

            newVideos.Add(target);
        }

        newVideos = SaveItems(newVideos, (Folder)primary.GetParent()).Cast<Video>().ToList();

        var newIds = new HashSet<Guid>(newVideos.Select(x => x.Id));
        var stale = existing.Values
            .Where(m =>
                !newIds.Contains(m.Id) &&
                (m.GelatoData<List<Guid>>("userIds")?.Contains(userId) ?? false)
            )
            .ToList();

        foreach (var _item in stale) {
            var users = _item.GelatoData<List<Guid>>("userIds") ?? new List<Guid>();

            if (users.Remove(userId)) {
                _item.SetGelatoData("userIds", users);
            }
            if (!users.Any()) {
                _library.DeleteItem(
              item,
              new DeleteOptions { DeleteFileLocation = true },
              true);
            }
        }

        _repo.SaveItems(stale, ct);
        newVideos.Add(primary);
        // MergeVersions(newVideos.ToArray());

        stopwatch.Stop();

        _log.LogInformation(
            $"SyncStreams finished GelatoId={uri.ExternalId} userId={userId} duration={Math.Round(stopwatch.Elapsed.TotalSeconds, 1)}s streams={newVideos.Count}"
        );

        return acceptable.Count;
    }

    public async Task MergeVersions(Video[] items) {
        if (items == null || items.Length < 2) {
            _log.LogWarning("MergeVersions called with insufficient items.");
            return;
        }

        // try to get a persistsnt value
        var primaryVersion =
            items.FirstOrDefault(i => string.IsNullOrEmpty(i.PrimaryVersionId))
            //items.FirstOrDefault(i => i.Path?.StartsWith("stremio", StringComparison.OrdinalIgnoreCase) == true)
            // ?? items.FirstOrDefault(i => i.IsFileProtocol)
            //?? items.FirstOrDefault(i => i.MediaSourceCount > 1 && string.IsNullOrEmpty(i.PrimaryVersionId))
            ?? items.FirstOrDefault();

        if (primaryVersion == null) {
            _log.LogError(
                "MergeVersions: No item with a path starting with 'stremio' found. Merge aborted."
            );
            return;
        }

        _log.LogDebug($"selected {primaryVersion.Name} {primaryVersion.Id} as primary version");

        var inv = CultureInfo.InvariantCulture;
        var alternates = items.Where(i => !i.Id.Equals(primaryVersion.Id)).ToList();
        var replacementLinks = alternates
            .Select(i => new LinkedChild { Path = i.Path, ItemId = i.Id })
            .ToArray();

        foreach (var v in alternates) {
            v.SetPrimaryVersionId(primaryVersion.Id.ToString("N", inv));
            v.LinkedAlternateVersions = Array.Empty<LinkedChild>();

            await v.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
                .ConfigureAwait(false);
        }

        primaryVersion.LinkedAlternateVersions = replacementLinks;
        primaryVersion.SetPrimaryVersionId(null);

        await primaryVersion
            .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private void AddToAlternateVersionsIfNotPresent(
        List<LinkedChild> alternateVersions,
        LinkedChild newVersion
    ) {
        if (
            !alternateVersions.Any(i =>
                string.Equals(i.Path, newVersion.Path, StringComparison.OrdinalIgnoreCase)
            )
        ) {
            alternateVersions.Add(newVersion);
        }
    }

    public String GetStrmPath(string basePath, BaseItem item) {
        var dirInfo = new DirectoryInfo(basePath);
        var baseName = $"{item.Name} ({item.PremiereDate?.Year})";
        var fileName = IsStream((Video)item)
            ? $"{baseName} {item.GelatoData<Guid>("guid")}.strm"
            : $"{baseName}.strm";

        return Path.Combine(dirInfo.FullName, fileName);
    }

    /// <summary>
    /// Builds a proper .strm path for a non-folder item using the parent path.
    /// Movies: parent/MovieName (Year)/MovieName (Year).strm
    /// Episodes: parent/EpisodeName.strm  (parent is the season folder)
    /// Streams: appends guid to disambiguate.
    /// </summary>
    private string BuildStrmPath(BaseItem item, Folder parent) {
        var year = item.PremiereDate?.Year;
        var baseName = year.HasValue
            ? $"{item.Name} ({year})"
            : item.Name;

        var isStream = item is Video v && IsStream(v);

        // Use the original filename from the stream's BehaviorHints if available
        var hintFilename = item.GelatoData<string>("filename");
        if (isStream && !string.IsNullOrEmpty(hintFilename)) {
            var fileName = Path.ChangeExtension(hintFilename, ".strm");
            if (item.GetBaseItemKind() == BaseItemKind.Movie)
                return Path.Combine(parent.Path, baseName, fileName);
            return Path.Combine(parent.Path, fileName);
        }

        var generatedFileName = isStream
            ? $"{baseName} {item.GelatoData<Guid>("guid")}.strm"
            : $"{baseName}.strm";

        // Movies get their own subdirectory: MovieName (Year)/MovieName (Year).strm
        if (item.GetBaseItemKind() == BaseItemKind.Movie)
            return Path.Combine(parent.Path, baseName, generatedFileName);

        return Path.Combine(parent.Path, generatedFileName);
    }

    public static void CreateStrmFile(string path, string content) {
        var directory = Path.GetDirectoryName(path);
        //   Console.WriteLine(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        if (!path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            path += ".strm";

        File.WriteAllText(path, content.Trim(), Encoding.UTF8);
    }

    public bool IsPrimaryVersion(Video item) {
        return !HasStreamTag(item) && string.IsNullOrWhiteSpace(item.PrimaryVersionId);
    }

    public static bool HasStreamTag(BaseItem item) {
        return item.Tags is not null && item.Tags.Contains(StreamTag, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsStream(Video item) {
        return IsGelato(item) && !IsPrimaryVersion(item);
    }

    private static bool IsLocalFile(string? path) {
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
    public virtual bool CanDelete(BaseItem item, User user) {
        var allCollectionFolders = _library.GetUserRootFolder().Children.OfType<Folder>().ToList();

        return item.IsAuthorizedToDelete(user, allCollectionFolders);
    }

    public bool IsStremio(BaseItem item) {
        return IsGelato(item);
    }

    public bool IsGelato(BaseItem item) {
        var stremioId = item.GetProviderId("Stremio");
        if (
            !string.IsNullOrWhiteSpace(stremioId)
        // failsafe. local file is never gelato
        // && !(
        //    (
        //        item.GetBaseItemKind() == BaseItemKind.Movie
        //        || item.GetBaseItemKind() == BaseItemKind.Episode
        //    ) && item.IsFileProtocol
        // )
        ) {
            return true;
        }
        return false;
    }

    public string GetGelatoLocalPath(BaseItem item, string? fileName = null) {
        var basePath =
            item.GetBaseItemKind() == BaseItemKind.Movie
                ? GelatoPlugin.Instance!.Configuration.MoviePath
                : GelatoPlugin.Instance!.Configuration.SeriesPath;

        if (item.GetBaseItemKind() == BaseItemKind.Episode) {
            var episode = (Episode)item;
            var series = episode.Series;

            if (series == null) {
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

        if (item.GetBaseItemKind() == BaseItemKind.Movie) {
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

    public void ReplaceGuid(ActionContext ctx, Guid value) {
        // Replace route values
        var rd = ctx.RouteData.Values;
        foreach (var key in new[] { "id", "Id", "ID", "itemId", "ItemId", "ItemID" }) {
            if (rd.TryGetValue(key, out var raw) && raw is not null) {
                // _log.LogInformation("Gelato: Replacing route {Key} {Old} → {New}", key, raw, value);
                ctx.RouteData.Values[key] = value.ToString();
            }
        }

        // Replace query string "ids"
        var request = ctx.HttpContext.Request;
        var parsed = QueryHelpers.ParseQuery(request.QueryString.Value ?? "");

        if (parsed.TryGetValue("ids", out var existing) && existing.Count == 1) {
            // _log.LogInformation("Gelato: Replacing query ids {Old} → {New}", existing[0], value);

            var dict = new Dictionary<string, StringValues>(parsed) {
                ["ids"] = new StringValues(value.ToString()),
            };

            ctx.HttpContext.Request.QueryString = QueryString.Create(dict);
        }
    }

    public async Task<BaseItem?> SyncSeriesTreesAsync(
        // Folder seriesRootFolder,
        PluginConfiguration cfg,
        StremioMeta seriesMeta,
        CancellationToken ct
    ) {
        var seriesRootFolder = cfg.SeriesFolder;
        // Early validation
        if (seriesRootFolder is null || string.IsNullOrWhiteSpace(seriesRootFolder.Path)) {
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

        if (seasonGroups.Count == 0) {
            _log.LogWarning("No valid episodes found for {SeriesId}", seriesMeta.Id);
            return null;
        }

        // Create or get series
        var tmpSeries = (Series)IntoBaseItem(seriesMeta);

        if (tmpSeries.ProviderIds is null || tmpSeries.ProviderIds.Count == 0) {
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
        if (series is null) {
            series = tmpSeries;
            if (series.Id == Guid.Empty)
                series.Id = Guid.NewGuid();

            var options = new MetadataRefreshOptions(_directoryService) {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllImages = false,
                ReplaceAllMetadata = true,
                ForceSave = true,
            };
            // important
            // series.ParentId = seriesRootFolder.Id;

            //seriesRootFolder.AddChild(series);
            SaveItem(series, seriesRootFolder);
            await series.RefreshMetadata(options, ct).ConfigureAwait(false);
            await series.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, ct);
        }

        var existingSeasonsDict = _library
            .GetItemList(
                new InternalItemsQuery {
                    ParentId = series.Id,
                    IncludeItemTypes = new[] { BaseItemKind.Season },
                    Recursive = true,
                    IsDeadPerson = true,
                }
            )
            .OfType<Season>()
            .Where(s => s.IndexNumber.HasValue)
            .GroupBy(s => s.IndexNumber!.Value)
            .Select(g => {
                if (g.Count() > 1) {
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

        foreach (var seasonGroup in seasonGroups) {
            ct.ThrowIfCancellationRequested();

            var seasonIndex = seasonGroup.Key;
            var seasonPath = $"{series.Path}:{seasonIndex}";

            if (!existingSeasonsDict.TryGetValue(seasonIndex, out var season)) {
                _log.LogTrace(
                    "Creating series {SeriesName} season {SeasonIndex:D2}",
                    series.Name,
                    seasonIndex
                );

                season = new Season {
                    Id = Guid.NewGuid(),
                    Name = $"Season {seasonIndex:D2}",
                    IndexNumber = seasonIndex,
                    SeriesId = series.Id,
                    SeriesName = series.Name,
                    Path = seasonPath,
                    DateLastRefreshed = DateTime.UtcNow,
                    SeriesPresentationUniqueKey = seriesPresentationKey,
                    DateModified = DateTime.UtcNow,
                    DateLastSaved = DateTime.UtcNow,
                    // important
                    ParentId = series.Id
                };
                //season.Path = $"{series.Path}/{season.Name}";
                // season.Id = _library.GetNewItemId(season.Path, season.GetType());

                season.SetProviderId("Stremio", $"{seriesStremioId}:{seasonIndex}");
                //season.SetProviderId(MetadataProvider.Custom, season.Id.ToString());
                season.PresentationUniqueKey = season.CreatePresentationUniqueKey();
                //series.AddChild(season);
                SaveItem(season, series);
                seasonsInserted++;

                //_log.LogInformation("Created season with id {SeasonId} and index {Index}", season.Id, season.IndexNumber);
            }

            // Get existing episodes once per season and create dictionary
            var existingEpisodeNumbers = _library
                .GetItemList(
                    new InternalItemsQuery {
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

            foreach (var epMeta in seasonGroup) {
                ct.ThrowIfCancellationRequested();

                var index = epMeta.Episode ?? epMeta.Number;

                // This should never happen due to earlier filtering, but kept for safety
                if (!index.HasValue) {
                    _log.LogWarning(
                        "Episode number missing for: {EpisodeName}, skipping",
                        epMeta.GetName()
                    );
                    continue;
                }

                if (existingEpisodeNumbers.Contains(index.Value)) {
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
                var episode = (Episode)IntoBaseItem(epMeta);

                episode.IndexNumber = index;
                episode.ParentIndexNumber = season.IndexNumber;
                episode.SeasonId = season.Id;
                episode.SeriesId = series.Id;
                episode.SeriesName = series.Name;
                episode.SeasonName = season.Name;
                episode.ParentId = season.Id;
                episode.SeriesPresentationUniqueKey = season.SeriesPresentationUniqueKey;
                episode.PresentationUniqueKey = episode.GetPresentationUniqueKey();
                //season.AddChild(episode);
                SaveItem(episode, season);
                // await episode.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, ct);
                episodesInserted++;
                _log.LogTrace("Created episode {EpisodeName}", epMeta.GetName());
            }
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

    public async Task SyncSeries(
        bool runningOnly,
        Guid userId,
        IProgress<double> progress,
        CancellationToken cancellationToken
    ) {
        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        // var seriesFolder = cfg.SeriesFolder;
        var seriesItems = _library
            .GetItemList(
                new InternalItemsQuery {
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

        var stremio = cfg.stremio;

        var processed = 0;
        foreach (var series in seriesItems) {
            cancellationToken.ThrowIfCancellationRequested();
            try {
                _log.LogDebug(
                    "SyncSeries: syncing series trees for {Name} ({Id})",
                    series.Name,
                    series.Id
                );

                var meta = await stremio.GetMetaAsync(series).ConfigureAwait(false);
                if (meta is null) {
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
            catch (Exception ex) {
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

    public BaseItem SaveItem(BaseItem item, Folder parent) {
        return SaveItems(new[] { item }, parent).FirstOrDefault();
    }

    public IEnumerable<BaseItem> SaveItems(IEnumerable<BaseItem> items, Folder parent) {
        foreach (var item in items) {

            if (item.IsFolder) {
                if (item.GetBaseItemKind() == BaseItemKind.Series) {
                    var year = item.PremiereDate?.Year;
                    item.Path = year.HasValue
                        ? Path.Combine(parent.Path, $"{item.Name} ({year})")
                        : Path.Combine(parent.Path, item.Name);
                }
                else {
                    item.Path = Path.Combine(parent.Path, item.Name);
                }
                Directory.CreateDirectory(item.Path);

                var now = DateTime.UtcNow;
                item.DateLastRefreshed = now;
                item.DateLastSaved = now;
            }
            else {
                item.ShortcutPath = item.Path;
                item.IsShortcut = true;
                item.Path = BuildStrmPath(item, parent);
                // Ensure parent directory exists for resolvers that hit System.IO directly
                var dir = Path.GetDirectoryName(item.Path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                // Write the .strm file for videos (Movies/Episodes)
                if (item is Video)
                    CreateStrmFile(item.Path, item.ShortcutPath);

                var now = DateTime.UtcNow;
                item.DateModified = now;
                item.DateLastRefreshed = now;
                item.DateLastSaved = now;
            }

            item.Id = _library.GetNewItemId(item.Path, item.GetType());
            item.PresentationUniqueKey = item.CreatePresentationUniqueKey();

            parent.AddChild(item);

        }
        _repo.SaveItems(items.ToList(), CancellationToken.None);

        foreach (var item in items) {
            _library.RegisterItem(item);
        }
        ;
        return items;
    }

    public BaseItem IntoBaseItem(StremioMeta meta, Folder parent = null, bool createStrm = false) {
        BaseItem item;

        var Id = meta.Id;

        switch (meta.Type) {
            case StremioMediaType.Series:
                item = new Series { Id = _library.GetNewItemId(Id, typeof(Series)) };
                break;

            case StremioMediaType.Movie:
                item = new Movie { Id = _library.GetNewItemId(Id, typeof(Movie)) };
                break;

            case StremioMediaType.Episode:
                item = new Episode { Id = _library.GetNewItemId(Id, typeof(Episode)) };
                break;
            default:
                _log.LogWarning("unsupported type {type}", meta.Type);
                return null;
        }
            ;

        item.Name = meta.GetName();
        item.PremiereDate = meta.GetPremiereDate();
        item.Path = $"gelato://stub/{Id}";

        if (!string.IsNullOrWhiteSpace(meta.Runtime))
            item.RunTimeTicks = Utils.ParseToTicks(meta.Runtime);
        if (!string.IsNullOrWhiteSpace(meta.Description))
            item.Overview = meta.Description;

        // NOTICE: do this only for show and movie. cause the parent imdb is used for season abd episodes
        if (meta.Type is not StremioMediaType.Episode && !string.IsNullOrWhiteSpace(Id)) {
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

            foreach (var (prefix, provider, stripPrefix) in providerMappings) {
                if (Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                    var providerId = stripPrefix ? Id.Substring(prefix.Length) : Id;
                    item.SetProviderId(provider, providerId);
                    break;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(meta.ImdbId)) {
            item.SetProviderId(MetadataProvider.Imdb, meta.ImdbId);
        }

        var stremioUri = new StremioUri(meta.Type, meta.ImdbId ?? Id);
        item.SetProviderId("Stremio", stremioUri.ExternalId);
        item.IsVirtualItem = false;
        item.ProductionYear = meta.GetYear();

        item.Overview = meta.Description ?? meta.Overview;

        var primary = meta.Poster ?? meta.Thumbnail;
        if (!string.IsNullOrWhiteSpace(primary)) {
            item.ImageInfos = new List<ItemImageInfo>
            {
                new ItemImageInfo { Type = ImageType.Primary, Path = primary },
            }.ToArray();
        }
        item.PresentationUniqueKey = item.CreatePresentationUniqueKey();
        return item;
    }
}
