using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Providers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators;

public sealed class MediaSourceManagerDecorator(
    IMediaSourceManager inner,
    ILibraryManager libraryManager,
    ILogger<MediaSourceManagerDecorator> log,
    IHttpContextAccessor http,
    IUserDataManager userDataManager,
    IDirectoryService directoryService,
    IServerConfigurationManager config,
    //Lazy<ISubtitleManager> subtitleManager,
    Lazy<GelatoManager> manager,
    Lazy<SubtitleProvider> subtitleProvider,
    IMediaSegmentManager mediaSegmentManager,
    Lazy<IProviderManager> providerManager
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
    // Lazy: ProviderManager depends on ISubtitleManager, which depends on
    // IMediaSourceManager - this decorator.
    private readonly Lazy<IProviderManager> _providerManager = providerManager;

    // Jellyfin builds its metadata providers by type scanning and hands them to
    // IProviderManager; none are registered in the container. So the probe
    // provider has to be looked up there - injecting
    // IEnumerable<ICustomMetadataProvider<Video>> always resolves to an empty list.
    private ICustomMetadataProvider<Video>? FindProbeProvider(Video owner) =>
        _providerManager
            .Value.GetMetadataProviders<Video>(
                owner,
                _libraryManager.GetLibraryOptions(owner),
                includeDisabled: true
            )
            .OfType<ICustomMetadataProvider<Video>>()
            .FirstOrDefault(p => p.Name == "Probe Provider");

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
            (!cfg.EnableMixed && !IsGelatoPlaybackItem(item))
            || item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode)
        )
        {
            var own = _inner.GetStaticMediaSources(item, enablePathSubstitution, user);

            // A local movie keeps the stream rows linked while mixed mode was on. Jellyfin would
            // list them with their stream URLs and without the per-user filter.
            if (item is Video { LinkedAlternateVersions.Length: > 0 } localVideo)
            {
                var linkedStreams = GetStreamRowIds(GetStreamRows(localVideo));
                if (linkedStreams.Count > 0)
                {
                    return own.Where(s => !linkedStreams.Contains(s.Id)).ToList();
                }
            }

            return own;
        }

        // A stream row is one version of its movie/episode. Jellyfin 12's web client loads it as
        // an item when the version dropdown changes and rebuilds the dropdown from its sources.
        var isStreamRow = item.HasStreamTag();

        var uri = StremioUri.FromBaseItem(item);
        var actionName =
            ctx?.Items.TryGetValue("actionName", out var ao) == true ? ao as string : null;

        var allowSync = ctx.IsInsertableAction() && userId != Guid.Empty;
        var video = item as Video;
        var syncItemId = video?.PrimaryVersionId ?? item.Id;
        // With the creation date: an item deleted and inserted again gets the same id (its path
        // is hashed), but its rows went with it, so it must sync anew within StreamTTL.
        var cacheKey = $"{syncItemId}:{item.DateCreated.Ticks}";

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
        else if (uri is not null && !isStreamRow && !manager.HasStreamSync(cacheKey, syncItemId))
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

        var itemId = item.Id.ToString("N", CultureInfo.InvariantCulture);

        // A version, a stream row or a file merged in by hand, lists the versions of its movie.
        var primary = video?.PrimaryVersionId is { } primaryVersionId
            ? _libraryManager.GetItemById(primaryVersionId) as Video
            : video;
        var linkedVersions = primary is null
            ? []
            : _libraryManager.GetLinkedAlternateVersions(primary).ToList();
        if (
            linkedVersions.Count == 0
            && primary is not null
            && !isStreamRow
            && IsGelatoPlaybackItem(primary)
            && manager.RelinkOwnedRows(primary)
        )
        {
            linkedVersions = _libraryManager.GetLinkedAlternateVersions(primary).ToList();
        }
        var streamRows = linkedVersions
            .Where(v => v.HasStreamTag())
            .OrderBy(v => v.GelatoData<int?>("index") ?? int.MaxValue)
            .ToList();
        var streamRowIds = GetStreamRowIds(streamRows);

        // Jellyfin lists the linked stream rows itself, named after the item; they are added
        // below with their stream names, and only the ones this user has. A stream row's own
        // Jellyfin source is the row itself. A Gelato movie/episode has no media of its own, so
        // unless versions were merged in by hand, Jellyfin's list is skipped: building it costs
        // several queries per stream.
        // A stream row of a local movie lists the movie's own file too.
        var mediaOwner = isStreamRow ? primary : item;
        var hasOwnMedia =
            mediaOwner is not null
            && (!IsGelatoPlaybackItem(mediaOwner) || linkedVersions.Any(v => !v.HasStreamTag()));
        var sources = !hasOwnMedia
            ? []
            : _inner
                .GetStaticMediaSources(mediaOwner!, enablePathSubstitution, user)
                .Where(s => !streamRowIds.Contains(s.Id))
                .ToList();

        var versions = streamRows
            .Where(x =>
                userId == Guid.Empty
                || (x.GelatoData<List<Guid>>("userIds")?.Contains(userId) ?? false)
            )
            .Select(row =>
            {
                var source = GetVersionInfo(row, MediaSourceType.Grouping, user);

                if (user is not null)
                {
                    _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);
                }

                return (Row: row, Source: source);
            })
            .ToList();

        _log.LogDebug(
            "Found {Count} streams. UserId={UserId} ItemId={ItemId}",
            versions.Count,
            userId,
            item.Id
        );

        sources.AddRange(versions.Select(v => v.Source));

        if (isStreamRow)
        {
            // The requested version goes first: it becomes the Default source.
            var own =
                sources.FirstOrDefault(s => s.Id == itemId)
                ?? GetVersionInfo(item, MediaSourceType.Grouping, user);
            sources.Remove(own);
            sources.Insert(0, own);
        }

        if (sources.Count > 1)
        {
            // remove primary from list when there are streams
            sources = sources
                .Where(k =>
                    !(k.Path?.StartsWith("gelato", StringComparison.OrdinalIgnoreCase) ?? false)
                )
                .Where(k =>
                    !(k.Path?.StartsWith("stremio", StringComparison.OrdinalIgnoreCase) ?? false)
                )
                .ToList();
        }

        // failsafe. mediasources cannot be null
        if (sources.Count == 0)
        {
            sources.Add(GetVersionInfo(item, MediaSourceType.Default, user));
        }

        // A Gelato movie/episode has no media of its own, so its first stream takes its id and the
        // movie is one of its versions: clients that play the source with the item's id get the
        // first stream, and its watch state stays on the movie. Version pages list it with the
        // same id, the first stream's own page included, so picking it there opens the movie and
        // playing it there reports progress on the movie.
        var primaryId = primary?.Id.ToString("N", CultureInfo.InvariantCulture);
        if (
            primaryId is not null
            && sources.All(s => s.Id != primaryId)
            && versions.FirstOrDefault().Source is { } first
        )
        {
            first.Id = primaryId;
        }

        if (!isStreamRow && primary is not null && user is not null)
        {
            MoveResumedVersionFirst(sources, primary, versions, user);
        }

        foreach (var source in sources)
        {
            if (source.Type == MediaSourceType.Default)
                source.Type = MediaSourceType.Grouping;
        }
        sources[0].Type = MediaSourceType.Default;

        return sources;
    }

    /// <summary>
    /// The stream rows linked to a movie/episode as its versions. Read from the database: the
    /// instance at hand may be a copy whose links are out of date.
    /// </summary>
    private List<Video> GetStreamRows(Video? primary) =>
        primary is null
            ? []
            : _libraryManager
                .GetLinkedAlternateVersions(primary)
                .Where(v => v.HasStreamTag())
                .ToList();

    private static HashSet<string> GetStreamRowIds(IEnumerable<Video> rows) =>
        rows.Select(r => r.Id.ToString("N", CultureInfo.InvariantCulture)).ToHashSet();

    /// <summary>
    /// Puts the stream the user is part way through first, so clients preselect it. The resume
    /// point itself is shared: StreamUserDataSync copies it to the movie.
    /// </summary>
    private void MoveResumedVersionFirst(
        List<MediaSourceInfo> sources,
        Video primary,
        List<(Video Row, MediaSourceInfo Source)> versions,
        User user
    )
    {
        if (sources.Count < 2 || versions.Count == 0)
            return;

        // The source with the movie's id plays with the movie's own watch state.
        var primaryId = primary.Id.ToString("N", CultureInfo.InvariantCulture);
        var streams = versions.Where(v => v.Source.Id != primaryId).ToList();
        var userData = userDataManager.GetUserDataBatch(
            [primary, .. streams.Select(v => v.Row)],
            user
        );
        if (userData.GetValueOrDefault(primary.Id) is not { PlaybackPositionTicks: > 0 } movieData)
            return;

        var resumed = VersionPlaybackSelector.SelectMostRecentlyPlayed(
            streams,
            v => userData.GetValueOrDefault(v.Row.Id),
            data => data.PlaybackPositionTicks > 0
        );

        // The movie holds a copy of the stream's state, dated CopyOffset after the stream; it is
        // newer only when the stream with the movie's id was played since.
        if (
            resumed.Source is not { } source
            || (userData[resumed.Row.Id].LastPlayedDate ?? DateTime.MinValue)
                + StreamUserDataSync.CopyOffset
                < (movieData.LastPlayedDate ?? DateTime.MinValue)
            || ReferenceEquals(sources[0], source)
        )
        {
            return;
        }

        sources.Remove(source);
        sources.Insert(0, source);
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

        var selected = SelectByIdOrFirst(sources, mediaSourceId);
        if (selected is null)
            return sources;

        var owner = ResolveOwnerFor(selected, item);
        if (!IsGelatoPlaybackItem(owner))
        {
            // A local movie's linked stream rows are in Jellyfin's list too, with their real URLs.
            // They are played through their own source id, which the branch below handles. A file
            // merged into a Gelato movie is asked for itself: Jellyfin would probe the movie's
            // placeholder path otherwise.
            var streamRowIds = GetStreamRowIds(GetStreamRows(item as Video));
            var playbackSources = await _inner
                .GetPlaybackMediaSources(
                    IsGelatoPlaybackItem(item) ? owner : item,
                    user,
                    allowMediaProbe,
                    enablePathSubstitution,
                    ct
                )
                .ConfigureAwait(false);
            return playbackSources.Where(s => !streamRowIds.Contains(s.Id)).ToList();
        }

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

        // Stub path after probing is done so the real URL is never sent to clients.
        // Force File protocol so clients proxy through Jellyfin instead of direct-playing.
        if (ctx.GetActionName() == "GetPostedPlaybackInfo")
        {
            selected.Path = "/stub";
            selected.IsRemote = false;
            selected.Protocol = MediaProtocol.File;
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

        // Gelato's sources name their item in the ETag (the first stream carries the movie's id).
        // Jellyfin's own, like a file merged in as a version, use the item's id.
        BaseItem ResolveOwnerFor(MediaSourceInfo s, BaseItem fallback) =>
            (Guid.TryParse(s.ETag, out var etag) ? libraryManager.GetItemById(etag) : null)
            ?? (Guid.TryParse(s.Id, out var id) ? libraryManager.GetItemById(id) : null)
            ?? fallback;
    }

    private static bool IsGelatoPlaybackItem(BaseItem item) =>
        item.HasStreamTag()
        || (item.Path?.StartsWith("gelato://", StringComparison.OrdinalIgnoreCase) ?? false);

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
            MediaStreams = GetMediaStreamsWithExternalSubs(item),
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

        info.Bitrate = item.TotalBitrate;
        info.InferTotalBitrate();

        return info;
    }

    // Jellyfin's MediaInfoResolver.GetExternalStreamsAsync bails immediately when !video.IsFileProtocol
    // (stream items have http:// paths). This means external subtitle files saved to the internal
    // metadata folder are never discovered during library refresh and never written to the DB.
    // We work around this by scanning the metadata folder ourselves at playback time and merging
    // any matching subtitle files into the DB streams on the fly.
    private IReadOnlyList<MediaStream> GetMediaStreamsWithExternalSubs(BaseItem item)
    {
        var streams = _inner.GetMediaStreams(item.Id).ToList();

        var existingPaths = new HashSet<string>(
            streams.Where(s => s.Path != null).Select(s => s.Path!),
            StringComparer.OrdinalIgnoreCase
        );

        var nextIndex = streams.Count > 0 ? streams.Max(s => s.Index) + 1 : 0;

        foreach (var (file, langCode, codec) in item.GetGelatoSubtitleFiles())
        {
            if (existingPaths.Contains(file))
                continue;

            streams.Add(
                new MediaStream
                {
                    Type = MediaStreamType.Subtitle,
                    IsExternal = true,
                    IsExternalUrl = false,
                    SupportsExternalStream = true,
                    Path = file,
                    Language = langCode,
                    Codec = codec,
                    Index = nextIndex++,
                    IsDefault = false,
                    IsForced = false,
                    IsHearingImpaired = false,
                }
            );

            existingPaths.Add(file);
        }

        return streams;
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

            var options = new MetadataRefreshOptions(directoryService)
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            };

            var probeProvider = FindProbeProvider(owner);
            if (probeProvider is not null)
            {
                // Call the ffprobe provider directly instead of going through
                // RefreshMetadata.
                //
                // RefreshMetadata runs the whole metadata pipeline, and with
                // FullRefresh that includes ExecuteRemoteProviders - so every
                // stream probe also re-queried OMDb/TMDb for the item. On a
                // library browsed through Gelato that is a remote metadata
                // lookup per probe, and it is where the recurring
                // "Error in The Open Movie Database" JsonException spam comes
                // from: OMDb returns malformed JSON for some season payloads
                // and the probe drags that call along every time.
                //
                // The probe provider on its own does exactly what is wanted
                // here - read the container's streams - and nothing else. The
                // caller already persists the result with
                // UpdateToRepositoryAsync and runs segment providers itself, so
                // no other part of the pipeline is needed. It also keeps image
                // fetchers away from the item while its path points at the
                // temporary .strm file.
                await probeProvider.FetchAsync(owner, options, ct).ConfigureAwait(false);
            }
            else
            {
                // No probe provider resolved - fall back to the old path rather
                // than silently skipping the probe. Logged at information: this
                // path used to be taken on every probe without anyone noticing.
                _log.LogInformation(
                    "No probe provider available for {Id}, falling back to RefreshMetadata",
                    owner.Id
                );
                await owner.RefreshMetadata(options, ct).ConfigureAwait(false);
            }
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
}
