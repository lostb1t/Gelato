using System.Globalization;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Providers;
//using MediaBrowser.Providers.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Subtitles;
using Gelato.Services;
//using MediaBrowser.Providers.MediaInfo;

namespace Gelato.Decorators;

public sealed class MediaSourceManagerDecorator(
    IMediaSourceManager inner,
    ILibraryManager libraryManager,
    ILogger<MediaSourceManagerDecorator> log,
    IHttpContextAccessor http,
    GelatoItemRepository repo,
    IDirectoryService directoryService,
    IServerConfigurationManager config,
   ISubtitleManager subtitleManager,
    Lazy<GelatoManager> manager,
    IMediaSegmentManager mediaSegmentManager)
    : IMediaSourceManager {
    private readonly IMediaSourceManager _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly ILogger<MediaSourceManagerDecorator> _log = log ?? throw new ArgumentNullException(nameof(log));
    private readonly IHttpContextAccessor _http = http ?? throw new ArgumentNullException(nameof(http));
    private readonly KeyLock _lock = new();
    private readonly IMediaSegmentManager _mediaSegmentManager = mediaSegmentManager ?? throw new ArgumentNullException(nameof(mediaSegmentManager));
    private readonly ILibraryManager _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
    private readonly IServerConfigurationManager _config = config;
    private readonly ISubtitleManager _subtitleManager = subtitleManager;
    public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(
        BaseItem item,
        bool enablePathSubstitution,
        User? user = null
    ) {
        var manager1 = manager.Value;
        _log.LogDebug(
            "GetStaticMediaSources {Id}",
            item.Id
        );
        var ctx = _http.HttpContext;
        Guid userId;
        if (user != null) {
            userId = user.Id;
        } else {
            ctx.TryGetUserId(out userId);
        }

        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        if (
            (!cfg.EnableMixed && !manager1.IsGelato(item))
            || item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode)
        ) {
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

        if (userId != Guid.Empty) {
            cacheKey = $"{userId.ToString()}:{cacheKey}";
        }

        if (!allowSync) {
            _log.LogDebug(
                "GetStaticMediaSources not a sync-eligible call. action={Action} uri={Uri}",
                actionName,
                uri?.ToString()
            );
        }
        else if (uri is not null && !manager1.HasStreamSync(cacheKey)) {
            // Bug in web UI that calls the detail page twice. So that's why there's a lock.
            _lock
                .RunSingleFlightAsync(
                    item.Id,
                    async ct => {
                        _log.LogDebug(
                            "GetStaticMediaSources refreshing streams for {Id}",
                            item.Id
                        );
                        try {
                            var count = await manager1.SyncStreams(item, userId, ct).ConfigureAwait(false);
                            if (count > 0) {
                                manager1.SetStreamSync(cacheKey);
                            }
                        }
                        catch (Exception ex) {
                            _log.LogError(ex, "Failed to sync streams");
                        }
                    }
                )
                .GetAwaiter()
                .GetResult();

            // refresh item
            libraryManager.GetItemById(item.Id);
        }

        var sources = _inner.GetStaticMediaSources(item, enablePathSubstitution, user)
            .ToList();

        // we dont use jellyfins alternate versions crap. So we have to load it ourselves

        InternalItemsQuery query;

        if (item.GetBaseItemKind() == BaseItemKind.Episode) {
            var episode = (Episode)item;
            query = new InternalItemsQuery {
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
        else {
            query = new InternalItemsQuery {
                IncludeItemTypes = [item.GetBaseItemKind()],
                HasAnyProviderId = new Dictionary<string, string> { { "Stremio", item.GetProviderId("Stremio") } },
                Recursive = false,
                GroupByPresentationUniqueKey = false,
                GroupBySeriesPresentationUniqueKey = false,
                CollapseBoxSetItems = false,
                IsDeadPerson = true,
                Tags = [GelatoManager.StreamTag],
            };
        }

        var gelatoSources = repo
            .GetItemList(query)
            .OfType<Video>()
            .Where(x =>
                manager1.IsGelato(x) &&
                (
                    userId == Guid.Empty ||
                    (x.GelatoData<List<Guid>>("userIds")?.Contains(userId) ?? false)
                ))
            .OrderBy(x => x.GelatoData<int?>("index") ?? int.MaxValue)
            .Select(s => {
                var k = GetVersionInfo(
                    s,
                    MediaSourceType.Grouping,
                    ctx,
                    user
                );

                if (user is not null) {
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

        if (sources.Count > 1) {
            // remove primary from list when there are streams
            sources = sources
                .Where(k => !k.Path.StartsWith("gelato", StringComparison.OrdinalIgnoreCase))
                .Where(k => !k.Path.StartsWith("stremio", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // failsafe. mediasources cannot be null
        if (sources.Count == 0) {
            sources.Add(
                GetVersionInfo(item, MediaSourceType.Default, ctx, user)
            );
        }

        if (sources.Count > 0)
            sources[0].Type = MediaSourceType.Default;

        sources[0].Id = item.Id.ToString("N");

        return sources;
    }

    public void AddParts(IEnumerable<IMediaSourceProvider> providers) {
        _inner.AddParts(providers);
    }

    public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId) {
        return _inner.GetMediaStreams(itemId);
    }

    

    

    public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query) {
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
    ) {
        if (item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode)) {
            return await _inner
                .GetPlaybackMediaSources(
                    item,
                    user,
                    allowMediaProbe,
                    enablePathSubstitution,
                    ct
                )
                .ConfigureAwait(false);
        }

        var manager1 = manager.Value;
        var ctx = _http.HttpContext;

        var sources = GetStaticMediaSources(item, enablePathSubstitution, user);

        Guid? mediaSourceId =
            ctx?.Items.TryGetValue("MediaSourceId", out var idObj) == true
            && idObj is string idStr
            && Guid.TryParse(idStr, out var fromCtx)
                ? fromCtx
                : (
                    manager1.IsPrimaryVersion(item as Video)
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

        var selected = SelectByIdOrFirst(sources, mediaSourceId);
        if (selected is null)
            return sources;

        var owner = ResolveOwnerFor(selected, item);
        if (manager1.IsPrimaryVersion(owner as Video) && owner.Id != item.Id) {
            sources = GetStaticMediaSources(owner, enablePathSubstitution, user);
            selected = SelectByIdOrFirst(sources, mediaSourceId);
            if (selected is null)
                return sources;
        }

        if (NeedsProbe(selected)) {
            var libraryOptions = _libraryManager.GetLibraryOptions(owner);
            
            // Run segment providers and metadata refresh in parallel
            var segmentTask = _mediaSegmentManager.RunSegmentPluginProviders(owner, libraryOptions, false, ct);
            var metadataTask = owner.RefreshMetadata(
                new MetadataRefreshOptions(directoryService) {
                    EnableRemoteContentProbe = true,
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                },
                ct
            );
            var subtitleTask = DownloadSubtitles((Video)owner, ct);

            // Wait for both operations to complete
            await Task.WhenAll(segmentTask, metadataTask).ConfigureAwait(false);

            await owner
                .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                .ConfigureAwait(false);

            var refreshed = GetStaticMediaSources(item, enablePathSubstitution, user);
            selected = SelectByIdOrFirst(refreshed, mediaSourceId);

            if (selected is null)
                return refreshed;
        }

        if (item.RunTimeTicks is null && selected.RunTimeTicks is not null) {
            item.RunTimeTicks = selected.RunTimeTicks;
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                .ConfigureAwait(false);
        }

        return [selected];

        static MediaSourceInfo? SelectByIdOrFirst(IReadOnlyList<MediaSourceInfo> list, Guid? id) {
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

        BaseItem ResolveOwnerFor(MediaSourceInfo s, BaseItem fallback) =>
            Guid.TryParse(s.ETag, out var g)
                ? libraryManager.GetItemById(g) ?? fallback
                : fallback;
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

    public Task<MediaSourceInfo> GetLiveStream(
        string id,
        CancellationToken cancellationToken
    ) => _inner.GetLiveStream(id, cancellationToken);

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
    ) {
        ArgumentNullException.ThrowIfNull(item);

        var streamName = item.GelatoData<string>("name");
        var streamDesc = item.GelatoData<string>("description");
        var bingeGroup = item.GelatoData<string>("bingeGroup");
        var richName = !string.IsNullOrEmpty(streamDesc) ? $"{streamName}\n{streamDesc}" : streamName;

        var info = new MediaSourceInfo {
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
            HasSegments = true
            //HasSegments = MediaSegmentManager.HasSegments(item.Id)  
        };

        // Set custom HTTP header for binge group routing/load balancing in streaming requests for Anfiteatro client to serve binge group aware content.
        if (!string.IsNullOrEmpty(bingeGroup)) {
            info.RequiredHttpHeaders = new Dictionary<string, string>
            {
                { "X-Gelato-BingeGroup", bingeGroup }
            };
        }

        if (user is not null) {
            info.SupportsTranscoding = user.HasPermission(
                PermissionKind.EnableVideoPlaybackTranscoding
            );
            info.SupportsDirectStream = user.HasPermission(
                PermissionKind.EnablePlaybackRemuxing
            );
        }
        if (string.IsNullOrEmpty(info.Path)) {
            info.Type = MediaSourceType.Placeholder;
        }

        if (item is Video video) {
            info.IsoType = video.IsoType;
            info.VideoType = video.VideoType;
            info.Video3DFormat = video.Video3DFormat;
            info.Timestamp = video.Timestamp;
            info.IsRemote = true;

            if (video.IsShortcut) {
                info.IsRemote = true;
                info.Path = video.ShortcutPath;
            }
        }

        // massive cheat. clients will direct play remote files directly. But we always want to proxy it.
        // just fake a real file.
        if (ctx.GetActionName() == "GetPostedPlaybackInfo") {
            info.IsRemote = false;
            info.Protocol = MediaProtocol.File;
        }

        info.Bitrate = item.TotalBitrate;
        info.InferTotalBitrate();

        return info;
    }
    
    public async Task<bool> DownloadSubtitles(Video video, CancellationToken cancellationToken)
        {
            var mediaStreams = video.GetMediaStreams();
            var subtitleOptions = _config.GetConfiguration<SubtitleOptions>("subtitles");
            var libraryOptions = _libraryManager.GetLibraryOptions(video);

            string[] subtitleDownloadLanguages;
            bool skipIfEmbeddedSubtitlesPresent;
            bool skipIfAudioTrackMatches;
            bool requirePerfectMatch;

            if (libraryOptions.SubtitleDownloadLanguages is null)
            {
                subtitleDownloadLanguages = subtitleOptions.DownloadLanguages;
                skipIfEmbeddedSubtitlesPresent = subtitleOptions.SkipIfEmbeddedSubtitlesPresent;
                skipIfAudioTrackMatches = subtitleOptions.SkipIfAudioTrackMatches;
                requirePerfectMatch = subtitleOptions.RequirePerfectMatch;
            }
            else
            {
                subtitleDownloadLanguages = libraryOptions.SubtitleDownloadLanguages;
                skipIfEmbeddedSubtitlesPresent = libraryOptions.SkipSubtitlesIfEmbeddedSubtitlesPresent;
                skipIfAudioTrackMatches = libraryOptions.SkipSubtitlesIfAudioTrackMatches;
                requirePerfectMatch = libraryOptions.RequirePerfectSubtitleMatch;
            }
            
           // var searchResults = await _subtitleManager.SearchSubtitles(request, cancellationToken).ConfigureAwait(false);

           //     var result = searchResults.FirstOrDefault();

           //     if (result is not null)
           //     {
           //         await _subtitleManager.DownloadSubtitles(video, result.Id, cancellationToken).ConfigureAwait(false);

           //         return true;
           //     }
           // }

            var downloadedLanguages = await new SubtitleDownloadService(
                _log,
                _subtitleManager).DownloadSubtitles(
                    video,
                    mediaStreams,
                    skipIfEmbeddedSubtitlesPresent,
                    skipIfAudioTrackMatches,
                    requirePerfectMatch,
                    subtitleDownloadLanguages,
                    libraryOptions.DisabledSubtitleFetchers,
                    libraryOptions.SubtitleFetcherOrder,
                    true,
                    cancellationToken).ConfigureAwait(false);

            

            return true;
        }
}
