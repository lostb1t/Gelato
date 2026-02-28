using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using Gelato.Providers;
using Gelato.Services;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators;

public sealed class MediaSourceManagerDecorator(
    IMediaSourceManager inner,
    ILibraryManager libraryManager,
    ILogger<MediaSourceManagerDecorator> log,
    IHttpContextAccessor http,
    GelatoItemRepository repo,
    IDirectoryService directoryService,
    IServerConfigurationManager config,
    //Lazy<ISubtitleManager> subtitleManager,
    Lazy<GelatoManager> manager,
    Lazy<SubtitleProvider> subtitleProvider,
    IMediaSegmentManager mediaSegmentManager,
    IEnumerable<ICustomMetadataProvider<Video>> videoProbeProviders,
    Lazy<ISyncPlayManager> syncPlayManager,
    Lazy<ISessionManager> sessionManager
) : IMediaSourceManager
{
    private readonly IMediaSourceManager _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly ILogger<MediaSourceManagerDecorator> _log =
        log ?? throw new ArgumentNullException(nameof(log));
    private readonly IHttpContextAccessor _http =
        http ?? throw new ArgumentNullException(nameof(http));
    private readonly KeyLock _lock = new();
    private readonly IMediaSegmentManager _mediaSegmentManager =
        mediaSegmentManager ?? throw new ArgumentNullException(nameof(mediaSegmentManager));
    private readonly ILibraryManager _libraryManager =
        libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
    private readonly IServerConfigurationManager _config =
        config ?? throw new ArgumentNullException(nameof(config));
    private readonly Lazy<GelatoManager> _manager = manager;
    private readonly Lazy<SubtitleProvider> _subtitleProvider = subtitleProvider;

    //  private readonly Lazy<ISubtitleManager> _subtitleManager = subtitleManager ?? throw new ArgumentNullException(nameof(subtitleManager));
    private readonly Lazy<ISyncPlayManager> _syncPlayManager = syncPlayManager;
    private readonly Lazy<ISessionManager> _sessionManager = sessionManager;
    private readonly ICustomMetadataProvider<Video>? _probeProvider =
        videoProbeProviders.FirstOrDefault(p => p.Name == "Probe Provider");

    private static readonly ConcurrentDictionary<string, (string SourceId, DateTime Expiry)> _syncPlaySourceCache = new();
    private static readonly TimeSpan _syncPlayCacheTtl = TimeSpan.FromMinutes(10);

    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(
        BaseItem item,
        bool enablePathSubstitution,
        User? user = null
    )
    {
        var manager = _manager.Value;
        _log.LogDebug("GetStaticMediaSources {Id}", item.Id);
        var ctx = _http.HttpContext;
        Guid userId;
        if (user != null)
        {
            userId = user.Id;
        }
        else
        {
            ctx.TryGetUserId(out userId);
        }

        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        if (
            (!cfg.EnableMixed && !item.IsGelato())
            || item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode)
        )
        {
            return _inner.GetStaticMediaSources(item, enablePathSubstitution, user);
        }

        var uri = StremioUri.FromBaseItem(item);
        var actionName =
            ctx?.Items.TryGetValue("actionName", out var ao) == true ? ao as string : null;

        var allowSync = ctx.IsInsertableAction() && userId != Guid.Empty;
        var video = item as Video;
        var cacheKey = Guid.TryParse(video?.PrimaryVersionId, out var id)
            ? id.ToString()
            : item.Id.ToString();

        if (userId != Guid.Empty)
        {
            cacheKey = $"{userId.ToString()}:{cacheKey}";
        }

        if (!allowSync)
        {
            _log.LogDebug(
                "GetStaticMediaSources not a sync-eligible call. action={Action} uri={Uri}",
                actionName,
                uri?.ToString()
            );
        }
        else if (uri is not null && !manager.HasStreamSync(cacheKey))
        {
            // Bug in web UI that calls the detail page twice. So that's why there's a lock.
            _lock
                .RunSingleFlightAsync(
                    item.Id,
                    async ct =>
                    {
                        _log.LogDebug("GetStaticMediaSources refreshing streams for {Id}", item.Id);

                        // Prewarm subtitle cache in the background if Gelato Subtitles
                        // is enabled for this library.
                        var libraryOptions = _libraryManager.GetLibraryOptions(item);
                        var subtitlePrewarmEnabled =
                            libraryOptions.SubtitleDownloadLanguages?.Length > 0
                            && !libraryOptions.DisabledSubtitleFetchers.Contains(
                                "Gelato Subtitles",
                                StringComparer.OrdinalIgnoreCase
                            );

                        if (subtitlePrewarmEnabled)
                        {
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    await _subtitleProvider
                                        .Value.GetSubtitlesAsync(
                                            uri.ExternalId,
                                            uri.MediaType,
                                            CancellationToken.None
                                        )
                                        .ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    _log.LogWarning(ex, "Subtitle prewarm failed for {Uri}", uri);
                                }
                            });
                        }

                        try
                        {
                            var count = await manager
                                .SyncStreams(item, userId, ct)
                                .ConfigureAwait(false);
                            if (count > 0)
                            {
                                manager.SetStreamSync(cacheKey);
                            }
                        }
                        catch (Exception ex)
                        {
                            _log.LogError(ex, "Failed to sync streams");
                        }
                    }
                )
                .GetAwaiter()
                .GetResult();

            // refresh item
            libraryManager.GetItemById(item.Id);
        }

        var sources = _inner.GetStaticMediaSources(item, enablePathSubstitution, user).ToList();

        // we dont use jellyfins alternate versions crap. So we have to load it ourselves

        InternalItemsQuery query;

        if (item.GetBaseItemKind() == BaseItemKind.Episode)
        {
            var episode = (Episode)item;
            query = new InternalItemsQuery
            {
                IncludeItemTypes = [item.GetBaseItemKind()],
                ParentId = episode.SeasonId,
                Recursive = false,
                GroupByPresentationUniqueKey = false,
                GroupBySeriesPresentationUniqueKey = false,
                CollapseBoxSetItems = false,
                IsDeadPerson = true,
                Tags = [GelatoManager.StreamTag],
                IndexNumber = episode.IndexNumber,
            };
        }
        else
        {
            query = new InternalItemsQuery
            {
                IncludeItemTypes = [item.GetBaseItemKind()],
                HasAnyProviderId = new Dictionary<string, string>
                {
                    { "Stremio", item.GetProviderId("Stremio") },
                },
                Recursive = false,
                GroupByPresentationUniqueKey = false,
                GroupBySeriesPresentationUniqueKey = false,
                CollapseBoxSetItems = false,
                IsDeadPerson = true,
                Tags = [GelatoManager.StreamTag],
            };
        }

        // SyncPlay members must see ALL sources regardless of which user
        // triggered the catalog sync — the addon config is server-wide.
        var isSyncPlayUser = false;
        try { isSyncPlayUser = userId != Guid.Empty && _syncPlayManager.Value.IsUserActive(userId); }
        catch { /* ignore — not critical */ }

        var gelatoSources = repo.GetItemList(query)
            .OfType<Video>()
            .Where(x =>
                x.IsGelato()
                && (
                    userId == Guid.Empty
                    || isSyncPlayUser
                    || (x.GelatoData<List<Guid>>("userIds")?.Contains(userId) ?? false)
                )
            )
            .OrderBy(x => x.GelatoData<int?>("index") ?? int.MaxValue)
            .Select(s =>
            {
                var k = GetVersionInfo(s, MediaSourceType.Grouping, ctx, user);

                if (user is not null)
                {
                    _inner.SetDefaultAudioAndSubtitleStreamIndices(item, k, user);
                }

                return k;
            })
            .ToList();

        _log.LogDebug(
            "Found {s} streams. UserId={Action} GelatoId={Uri}",
            gelatoSources.Count,
            userId,
            item.GetProviderId("Stremio")
        );

        sources.AddRange(gelatoSources);

        if (sources.Count > 1)
        {
            // remove primary from list when there are streams
            sources = sources
                .Where(k => !(k.Path?.StartsWith("gelato", StringComparison.OrdinalIgnoreCase) ?? false))
                .Where(k => !(k.Path?.StartsWith("stremio", StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }

        // failsafe. mediasources cannot be null
        if (sources.Count == 0)
        {
            sources.Add(GetVersionInfo(item, MediaSourceType.Default, ctx, user));
        }

        if (sources.Count > 0)
            sources[0].Type = MediaSourceType.Default;

        sources[0].Id = item.Id.ToString("N");

        return sources;
    }

    public void AddParts(IEnumerable<IMediaSourceProvider> providers)
    {
        _inner.AddParts(providers);
    }

    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
    {
        return _inner.GetMediaStreams(itemId);
    }

    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query)
    {
        return _inner.GetMediaStreams(query).ToList();
    }

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) =>
        _inner.GetMediaAttachments(itemId);

    public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) =>
        _inner.GetMediaAttachments(query);

    public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
        BaseItem item,
        User user,
        bool allowMediaProbe,
        bool enablePathSubstitution,
        CancellationToken ct
    )
    {
        if (item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode))
        {
            return await _inner
                .GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, ct)
                .ConfigureAwait(false);
        }

        var manager = _manager.Value;
        var ctx = _http.HttpContext;

        var sources = GetStaticMediaSources(item, enablePathSubstitution, user);

        Guid? mediaSourceId =
            ctx?.Items.TryGetValue("MediaSourceId", out var idObj) == true
            && idObj is string idStr
            && Guid.TryParse(idStr, out var fromCtx)
                ? fromCtx
                : (
                    item.IsPrimaryVersion()
                    && sources.Count > 0
                    && Guid.TryParse(sources[0].Id, out var fromSource)
                        ? fromSource
                        : null
                );

        _log.LogDebug(
            "GetPlaybackMediaSources {ItemId} mediaSourceId={MediaSourceId}",
            item.Id,
            mediaSourceId
        );

        // SyncPlay: ensure all group members use the same media source.
        // SyncPlayGroupFilter maintains a userId→groupId mapping so we
        // can scope the cache per group (avoids cross-group pollution
        // when two groups watch the same item simultaneously).
        var isSyncPlay = false;
        string? syncPlayCacheKey = null;
        string? prevSyncPlaySourceId = null;
        var hadExplicitSource = ctx?.Items.ContainsKey("MediaSourceId") == true;

        // user can be null when called from streaming endpoints (HLS, etc.)
        if (user is not null)
        {
            try
            {
                isSyncPlay = _syncPlayManager.Value.IsUserActive(user.Id);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "SyncPlay: IsUserActive check failed for {UserId}", user.Id);
            }

            _log.LogInformation(
                "SyncPlay: user={UserId} isSyncPlay={IsSyncPlay} hadExplicit={HadExplicit} mediaSourceId={MediaSourceId} item={ItemId}",
                user.Id, isSyncPlay, hadExplicitSource, mediaSourceId, item.Id);
        }

        if (isSyncPlay)
        {
            try
            {
                var groupId = SyncPlayGroupTracker.GetGroupForUser(user.Id);
                var groupPart = groupId?.ToString("N") ?? "unknown";
                syncPlayCacheKey = $"sp:{groupPart}:{item.Id:N}";
                var itemIdStr = item.Id.ToString("N");

                _log.LogInformation(
                    "SyncPlay: cacheKey={CacheKey} groupId={GroupId} userId={UserId}",
                    syncPlayCacheKey, groupId, user.Id);

                // Remember previous cached source to detect version switches later.
                if (_syncPlaySourceCache.TryGetValue(syncPlayCacheKey, out var prev)
                    && prev.Expiry > DateTime.UtcNow)
                {
                    prevSyncPlaySourceId = prev.SourceId;
                    _log.LogInformation(
                        "SyncPlay: found cached source {SourceId} for key {CacheKey}",
                        prevSyncPlaySourceId, syncPlayCacheKey);
                }

                // If the cache is empty (group just created/joined), seed
                // from the group leader's session so joiners get the same
                // source. Only seed with a specific stream ID — the item's
                // own ID is the default and caching it would block version
                // switching.
                if (prevSyncPlaySourceId is null)
                {
                    // Look at ALL group members' sessions, not just this user.
                    var groupSessions = _sessionManager.Value.Sessions
                        .Where(s => SyncPlayGroupTracker.GetGroupForUser(s.UserId) == groupId
                                    && s.NowPlayingItem is not null);
                    string? seedSourceId = null;
                    foreach (var gs in groupSessions)
                    {
                        var msid = gs.PlayState?.MediaSourceId;
                        if (!string.IsNullOrEmpty(msid) && msid != itemIdStr)
                        {
                            seedSourceId = msid;
                            break;
                        }
                    }

                    _log.LogInformation(
                        "SyncPlay: cache empty, seed from group sessions={SeedSourceId} for user {UserId}",
                        seedSourceId ?? "(null/default)", user.Id);

                    if (seedSourceId is not null)
                    {
                        prevSyncPlaySourceId = seedSourceId;
                        _syncPlaySourceCache[syncPlayCacheKey] =
                            (seedSourceId, DateTime.UtcNow + _syncPlayCacheTtl);
                    }
                }

                // Only override mediaSourceId when the cache holds a
                // specific stream ID. The item's own ID is the default
                // (sources[0]) and would prevent users from switching.
                if (!hadExplicitSource
                    && prevSyncPlaySourceId is not null
                    && prevSyncPlaySourceId != itemIdStr
                    && Guid.TryParse(prevSyncPlaySourceId, out var cachedId))
                {
                    _log.LogInformation(
                        "SyncPlay: overriding mediaSourceId to cached {SourceId} for item {ItemId}",
                        cachedId, item.Id);
                    mediaSourceId = cachedId;
                }
                else
                {
                    _log.LogInformation(
                        "SyncPlay: no override (cached={Cached} itemId={ItemId} hadExplicit={HadExplicit})",
                        prevSyncPlaySourceId ?? "(null)", itemIdStr, hadExplicitSource);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "SyncPlay: cache lookup failed");
            }
        }

        var selected = SelectByIdOrFirst(sources, mediaSourceId);

        // Guard: never return a gelato:// or stremio:// virtual source.
        // GetStaticMediaSources overwrites sources[0].Id to the item's own
        // ID, so a cached SyncPlay sourceId can match the virtual source
        // when no real streams are in the list yet.
        if (selected is not null && IsVirtualSource(selected))
        {
            _log.LogWarning(
                "GetPlaybackMediaSources: selected source {Id} has virtual path, falling back",
                selected.Id);
            selected = sources.FirstOrDefault(s => !IsVirtualSource(s))
                       ?? sources.FirstOrDefault();
            if (syncPlayCacheKey is not null)
                _syncPlaySourceCache.TryRemove(syncPlayCacheKey, out _);
        }

        if (selected is null)
            return sources;

        // Cache the resolved source so other SyncPlay members pick it up.
        // If a user explicitly switched version, push the change to other
        // group members so they don't stay on the old stream.
        if (isSyncPlay && syncPlayCacheKey is not null && user is not null)
        {
            try
            {
                var itemIdStr2 = item.Id.ToString("N");

                // Only cache a *specific* stream ID — the item's own ID
                // is the default (sources[0]) and caching it would block
                // version switching because SyncPlay commands never carry
                // mediaSourceId.
                if (selected.Id != itemIdStr2)
                {
                    _syncPlaySourceCache[syncPlayCacheKey] =
                        (selected.Id, DateTime.UtcNow + _syncPlayCacheTtl);

                    _log.LogInformation(
                        "SyncPlay: cached source {SourceId} for key {CacheKey}",
                        selected.Id, syncPlayCacheKey);
                }
                else
                {
                    _log.LogInformation(
                        "SyncPlay: NOT caching item's own ID {ItemId} for key {CacheKey}",
                        itemIdStr2, syncPlayCacheKey);
                }

                if (hadExplicitSource
                    && prevSyncPlaySourceId is not null
                    && selected.Id != prevSyncPlaySourceId)
                {
                    _ = PropagateSourceToGroupAsync(
                        user.Id, item.Id, selected.Id, ct);
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "SyncPlay: cache/propagate failed");
            }
        }

        var owner = ResolveOwnerFor(selected, item);
        if (owner.IsPrimaryVersion() && owner.Id != item.Id)
        {
            sources = GetStaticMediaSources(owner, enablePathSubstitution, user);
            selected = SelectByIdOrFirst(sources, mediaSourceId);
            if (selected is null)
                return sources;
        }

        if (NeedsProbe(selected))
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(owner);

            var segmentTask = _mediaSegmentManager.RunSegmentPluginProviders(
                owner,
                libraryOptions,
                false,
                ct
            );
            var metadataTask = ProbeStreamAsync((Video)owner, selected.Path, ct);
            //  var subtitleTask = DownloadSubtitles((Video)owner, ct);

            await Task.WhenAll(metadataTask, segmentTask).ConfigureAwait(false);

            await owner
                .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                .ConfigureAwait(false);

            var refreshed = GetStaticMediaSources(item, enablePathSubstitution, user);
            selected = SelectByIdOrFirst(refreshed, mediaSourceId);

            if (selected is null)
                return refreshed;
        }

        if (item.RunTimeTicks is null && selected.RunTimeTicks is not null)
        {
            item.RunTimeTicks = selected.RunTimeTicks;
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                .ConfigureAwait(false);
        }

        return [selected];

        static MediaSourceInfo? SelectByIdOrFirst(IReadOnlyList<MediaSourceInfo> list, Guid? id)
        {
            if (!id.HasValue)
                return list.FirstOrDefault();

            var target = id.Value;

            return list.FirstOrDefault(s =>
                    !string.IsNullOrEmpty(s.Id) && Guid.TryParse(s.Id, out var g) && g == target
                ) ?? list.FirstOrDefault();
        }

        static bool NeedsProbe(MediaSourceInfo s) =>
            (s.MediaStreams?.All(ms => ms.Type != MediaStreamType.Video) ?? true)
            || (s.RunTimeTicks ?? 0) < TimeSpan.FromMinutes(2).Ticks;

        static bool IsVirtualSource(MediaSourceInfo s) =>
            s.Path?.StartsWith("gelato", StringComparison.OrdinalIgnoreCase) == true
            || s.Path?.StartsWith("stremio", StringComparison.OrdinalIgnoreCase) == true;

        BaseItem ResolveOwnerFor(MediaSourceInfo s, BaseItem fallback) =>
            Guid.TryParse(s.ETag, out var g) ? libraryManager.GetItemById(g) ?? fallback : fallback;
    }

    public Task<MediaSourceInfo> GetMediaSource(
        BaseItem item,
        string mediaSourceId,
        string? liveStreamId,
        bool enablePathSubstitution,
        CancellationToken cancellationToken
    ) =>
        _inner.GetMediaSource(
            item,
            mediaSourceId,
            liveStreamId,
            enablePathSubstitution,
            cancellationToken
        );

    public async Task<LiveStreamResponse> OpenLiveStream(
        LiveStreamRequest request,
        CancellationToken cancellationToken
    ) => await _inner.OpenLiveStream(request, cancellationToken);

    public async Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(
        LiveStreamRequest request,
        CancellationToken cancellationToken
    ) => await _inner.OpenLiveStreamInternal(request, cancellationToken);

    public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken) =>
        _inner.GetLiveStream(id, cancellationToken);

    public Task<
        Tuple<MediaSourceInfo, IDirectStreamProvider>
    > GetLiveStreamWithDirectStreamProvider(string id, CancellationToken cancellationToken) =>
        _inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);

    public ILiveStream GetLiveStreamInfo(string id) => _inner.GetLiveStreamInfo(id);

    public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) =>
        _inner.GetLiveStreamInfoByUniqueId(uniqueId);

    public async Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(
        ActiveRecordingInfo info,
        CancellationToken cancellationToken
    ) => await _inner.GetRecordingStreamMediaSources(info, cancellationToken);

    public Task CloseLiveStream(string id) => _inner.CloseLiveStream(id);

    public async Task<MediaSourceInfo> GetLiveStreamMediaInfo(
        string id,
        CancellationToken cancellationToken
    ) => await _inner.GetLiveStreamMediaInfo(id, cancellationToken);

    public bool SupportsDirectStream(string path, MediaProtocol protocol) =>
        _inner.SupportsDirectStream(path, protocol);

    public MediaProtocol GetPathProtocol(string path) => _inner.GetPathProtocol(path);

    public void SetDefaultAudioAndSubtitleStreamIndices(
        BaseItem item,
        MediaSourceInfo source,
        User user
    ) => _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);

    public Task AddMediaInfoWithProbe(
        MediaSourceInfo mediaSource,
        bool isAudio,
        string cacheKey,
        bool addProbeDelay,
        bool isLiveStream,
        CancellationToken cancellationToken
    ) =>
        _inner.AddMediaInfoWithProbe(
            mediaSource,
            isAudio,
            cacheKey,
            addProbeDelay,
            isLiveStream,
            cancellationToken
        );

    private MediaSourceInfo GetVersionInfo(
        BaseItem item,
        MediaSourceType type,
        HttpContext ctx,
        User? user = null
    )
    {
        ArgumentNullException.ThrowIfNull(item);

        var streamName = item.GelatoData<string>("name");
        var streamDesc = item.GelatoData<string>("description");
        var bingeGroup = item.GelatoData<string>("bingeGroup");
        var richName = !string.IsNullOrEmpty(streamDesc)
            ? $"{streamName}\n{streamDesc}"
            : streamName;

        var info = new MediaSourceInfo
        {
            Id = item.Id.ToString("N", CultureInfo.InvariantCulture),
            ETag = item.Id.ToString("N", CultureInfo.InvariantCulture),
            Protocol = MediaProtocol.Http,
            MediaStreams = _inner.GetMediaStreams(item.Id),
            MediaAttachments = _inner.GetMediaAttachments(item.Id),
            Name = richName,
            Path = item.Path,
            RunTimeTicks = item.RunTimeTicks,
            Container = item.Container,
            Size = item.Size,
            Type = type,
            SupportsDirectStream = true,
            SupportsDirectPlay = true,
            // just always say yes
            HasSegments = true,
            //HasSegments = MediaSegmentManager.HasSegments(item.Id)
        };

        // Set custom HTTP header for binge group routing/load balancing in streaming requests for Anfiteatro client to serve binge group aware content.
        if (!string.IsNullOrEmpty(bingeGroup))
        {
            info.RequiredHttpHeaders = new Dictionary<string, string>
            {
                { "X-Gelato-BingeGroup", bingeGroup },
            };
        }

        if (user is not null)
        {
            info.SupportsTranscoding = user.HasPermission(
                PermissionKind.EnableVideoPlaybackTranscoding
            );
            info.SupportsDirectStream = user.HasPermission(PermissionKind.EnablePlaybackRemuxing);
        }
        if (string.IsNullOrEmpty(info.Path))
        {
            info.Type = MediaSourceType.Placeholder;
        }

        if (item is Video video)
        {
            info.IsoType = video.IsoType;
            info.VideoType = video.VideoType;
            info.Video3DFormat = video.Video3DFormat;
            info.Timestamp = video.Timestamp;
            info.IsRemote = true;

            if (video.IsShortcut)
            {
                info.IsRemote = true;
                info.Path = video.ShortcutPath;
            }
        }

        // massive cheat. clients will direct play remote files directly. But we always want to proxy it.
        // just fake a real file.
        if (ctx.GetActionName() == "GetPostedPlaybackInfo")
        {
            info.IsRemote = false;
            info.Protocol = MediaProtocol.File;
        }

        info.Bitrate = item.TotalBitrate;
        info.InferTotalBitrate();

        return info;
    }

    private async Task ProbeStreamAsync(Video owner, string streamUrl, CancellationToken ct)
    {
        var gelatoFilename = owner.GelatoData<string>("filename");
        var strmBaseName = !string.IsNullOrEmpty(gelatoFilename)
            ? Path.GetFileNameWithoutExtension(gelatoFilename)
            : $"{owner.Id:N}";
        var tmpStrm = Path.Combine(Path.GetTempPath(), $"{strmBaseName}.strm");
        await File.WriteAllTextAsync(tmpStrm, streamUrl, ct).ConfigureAwait(false);

        var origPath = owner.Path;
        var origShortcut = owner.IsShortcut;
        owner.Path = tmpStrm;
        owner.IsShortcut = true;
        owner.DateModified = new FileInfo(tmpStrm).LastWriteTimeUtc;

        try
        {
            _log.LogInformation("Probing stream for {Id} via {Url}", owner.Id, streamUrl);
            await owner.RefreshMetadata(
                new MetadataRefreshOptions(directoryService)
                {
                    EnableRemoteContentProbe = true,
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                },
                ct
            );
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Stream probe failed for {Id}", owner.Id);
        }
        finally
        {
            owner.Path = origPath;
            owner.IsShortcut = origShortcut;
            try
            {
                File.Delete(tmpStrm);
            }
            catch
            { /* best effort */
            }
        }
    }

    /// <summary>
    /// Sends a PlayNow command to every other session in the same SyncPlay
    /// group, telling them to restart playback with the new media source.
    /// Fire-and-forget so we don't block the caller's request.
    /// </summary>
    private async Task PropagateSourceToGroupAsync(
        Guid initiatorUserId,
        Guid itemId,
        string newMediaSourceId,
        CancellationToken ct)
    {
        try
        {
            var groupId = SyncPlayGroupTracker.GetGroupForUser(initiatorUserId);
            if (groupId is null) return;

            // Find the initiator's session (needed as controllingSessionId).
            var initiatorSession = _sessionManager.Value.Sessions
                .FirstOrDefault(s => s.UserId == initiatorUserId);
            if (initiatorSession is null) return;

            // Find other group members' active sessions.
            var targets = _sessionManager.Value.Sessions
                .Where(s =>
                    s.UserId != initiatorUserId
                    && SyncPlayGroupTracker.GetGroupForUser(s.UserId) == groupId)
                .ToList();

            foreach (var target in targets)
            {
                var command = new PlayRequest
                {
                    ItemIds = [itemId],
                    MediaSourceId = newMediaSourceId,
                    PlayCommand = PlayCommand.PlayNow,
                    StartPositionTicks = target.PlayState?.PositionTicks,
                    ControllingUserId = initiatorUserId,
                };

                await _sessionManager.Value.SendPlayCommand(
                    initiatorSession.Id,
                    target.Id,
                    command,
                    ct).ConfigureAwait(false);

                _log.LogDebug(
                    "SyncPlay: sent source switch to session {SessionId} " +
                    "(user {UserId}, source {SourceId})",
                    target.Id, target.UserId, newMediaSourceId);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "SyncPlay: failed to propagate source change to group members");
        }
    }
}
