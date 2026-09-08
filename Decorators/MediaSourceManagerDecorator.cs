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
using MediaBrowser.Controller.MediaEncoding;
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
    GelatoItemRepository repo,
    //Lazy<ISubtitleManager> subtitleManager,
    Lazy<GelatoManager> manager,
    Lazy<SubtitleProvider> subtitleProvider,
    IMediaSegmentManager mediaSegmentManager,
    IMediaEncoder mediaEncoder
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
    private readonly Lazy<GelatoManager> _manager = manager;
    private readonly Lazy<SubtitleProvider> _subtitleProvider = subtitleProvider;
    private readonly IMediaEncoder _mediaEncoder =
        mediaEncoder ?? throw new ArgumentNullException(nameof(mediaEncoder));

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
            return _inner.GetStaticMediaSources(item, enablePathSubstitution, user);
        }

        var uri = StremioUri.FromBaseItem(item);
        var actionName =
            ctx?.Items.TryGetValue("actionName", out var ao) == true ? ao as string : null;

        var allowSync = ctx.IsInsertableAction() && userId != Guid.Empty;
        var video = item as Video;
        var cacheKey = video?.PrimaryVersionId is Guid id && id != Guid.Empty
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
        var associationId = item.GetProviderId("Stremio");

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
            var associationUri = StremioUri.FromBaseItem(item);
            if (associationUri is null)
            {
                _log.LogDebug("No Stremio URI found for movie {ItemId}", item.Id);
                return sources;
            }

            associationId = associationUri.ExternalId;
            query = new InternalItemsQuery
            {
                IncludeItemTypes = [item.GetBaseItemKind()],
                HasAnyProviderId = new Dictionary<string, string>
                {
                    { "Stremio", associationUri.ExternalId },
                },
                Recursive = false,
                GroupByPresentationUniqueKey = false,
                GroupBySeriesPresentationUniqueKey = false,
                CollapseBoxSetItems = false,
                IsDeadPerson = true,
                Tags = [GelatoManager.StreamTag],
            };
        }

        var gelatoSources = repo.GetItemList(query)
            .OfType<Video>()
            .Where(x =>
                x.IsGelato()
                && (
                    userId == Guid.Empty
                    || (x.GelatoData<List<Guid>>("userIds")?.Contains(userId) ?? false)
                )
            )
            .OrderBy(x => x.GelatoData<int?>("index") ?? int.MaxValue)
            .Select(s =>
            {
                var k = GetVersionInfo(s, MediaSourceType.Grouping, user);

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
            associationId
        );

        sources.AddRange(gelatoSources);

        // Always drop the canonical "gelato://stub"/"stremio://" placeholder. It is the
        // "generic" entry with the same name as the movie and is not a real playable stream.
        sources = sources
            .Where(k =>
                !(k.Path?.StartsWith("gelato", StringComparison.OrdinalIgnoreCase) ?? false)
            )
            .Where(k =>
                !(k.Path?.StartsWith("stremio", StringComparison.OrdinalIgnoreCase) ?? false)
            )
            .ToList();

        // failsafe. mediasources cannot be null
        if (sources.Count == 0)
        {
            sources.Add(GetVersionInfo(item, MediaSourceType.Default, user));
        }

        // Jellyfin 12's web player reads MediaSources[0] from the item detail response,
        // so it must already have a real video stream. Probe the candidate streams here,
        // but start with the healthy streamvix source so we don't spend seconds probing
        // dead Mixdrop links.
        EnsureFirstPlayableSource(sources, item);

        if (sources.Count > 0)
            sources[0].Type = MediaSourceType.Default;

        sources[0].Id = item.Id.ToString("N");

        return sources;
    }

    private void EnsureFirstPlayableSource(List<MediaSourceInfo> sources, BaseItem item)
    {
        var candidates = sources
            .Where(s =>
                s.Path?.StartsWith("http", StringComparison.OrdinalIgnoreCase) ?? false
            )
            .ToList();

        // Generic preference: try HLS manifest URLs first. They are direct playlists and
        // generally the most reliable remote streams, regardless of the provider name.
        var preferred = candidates.FirstOrDefault(s =>
            s.Path?.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) == true
            || s.Path?.Contains(".m3u8?", StringComparison.OrdinalIgnoreCase) == true
        );
        var ordered = preferred is null
            ? candidates
            : new[] { preferred }.Concat(candidates.Where(c => !ReferenceEquals(c, preferred)));

        foreach (var candidate in ordered.Take(4))
        {
            var mediaInfo = ProbeSourceAsync(candidate, CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            if (mediaInfo?.MediaStreams?.Any(ms => ms.Type == MediaStreamType.Video) == true)
            {
                candidate.MediaStreams = mediaInfo.MediaStreams.ToList();
                candidate.Container = mediaInfo.Container;
                candidate.RunTimeTicks = mediaInfo.RunTimeTicks ?? candidate.RunTimeTicks;
                candidate.Size = mediaInfo.Size ?? candidate.Size;
                sources.Remove(candidate);
                sources.Insert(0, candidate);
                return;
            }
        }
    }

    private async Task<MediaInfo?> ProbeSourceAsync(MediaSourceInfo source, CancellationToken ct)
    {
        try
        {
            return await _mediaEncoder
                .GetMediaInfo(
                    new MediaInfoRequest
                    {
                        MediaSource = source,
                        ExtractChapters = false,
                        MediaType = DlnaProfileType.Video,
                    },
                    ct
                )
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Stream probe failed for {Path}", source.Path);
            return null;
        }
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

        // On Jellyfin 12 the repository's default ordering of the Gelato stream items
        // changed, so the "first" source can be a dead stream. Playback would fail on
        // that chosen source with no automatic fallback to one of the still-working
        // sources (e.g. a dead debrid link vs. a healthy provider). Probe the sources
        // in order and pick the first that actually has a video stream.
        var chosen = await PickWorkingSourceAsync(
                sources, item, user, enablePathSubstitution, ct
            )
            .ConfigureAwait(false);

        if (chosen is null)
            chosen = selected;

        if (item.RunTimeTicks is null && chosen.RunTimeTicks is not null)
        {
            item.RunTimeTicks = chosen.RunTimeTicks;
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                .ConfigureAwait(false);
        }

        // Stub path after probing is done so the real URL is never sent to clients.
        // Force File protocol so clients proxy through Jellyfin instead of direct-playing.
        if (ctx.GetActionName() == "GetPostedPlaybackInfo")
        {
            chosen.Path = "/stub";
            chosen.IsRemote = false;
            chosen.Protocol = MediaProtocol.File;
        }

        return [chosen];

        async Task<MediaSourceInfo?> PickWorkingSourceAsync(
            IReadOnlyList<MediaSourceInfo> candidates,
            BaseItem rootItem,
            User rootUser,
            bool rootEnablePathSubstitution,
            CancellationToken ct
        )
        {
            // Pass 1: prefer a source that already carries a real video stream (already
            // probed on a previous request) so we don't repeatedly probe dead streams.
            foreach (var source in candidates)
            {
                if (
                    source.MediaStreams?.Any(ms => ms.Type == MediaStreamType.Video) == true
                )
                {
                    return source;
                }
            }

            // Pass 2: probe the remaining sources in order and pick the first that yields
            // a video stream.
            foreach (var source in candidates)
            {
                var owner = ResolveOwnerFor(source, rootItem);
                if (!IsGelatoPlaybackItem(owner))
                    continue;

                var current = source;

                if (NeedsProbe(current))
                {
                    var mediaInfo = await ProbeSourceAsync(current, ct).ConfigureAwait(false);
                    if (mediaInfo?.MediaStreams?.Any(ms => ms.Type == MediaStreamType.Video) == true)
                    {
                        current.MediaStreams = mediaInfo.MediaStreams.ToList();
                        current.Container = mediaInfo.Container;
                        current.RunTimeTicks = mediaInfo.RunTimeTicks ?? current.RunTimeTicks;
                        current.Size = mediaInfo.Size ?? current.Size;
                    }
                }

                if (current.MediaStreams?.Any(ms => ms.Type == MediaStreamType.Video) == true)
                {
                    return current;
                }
            }

            return null;
        }

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

        BaseItem ResolveOwnerFor(MediaSourceInfo s, BaseItem fallback) =>
            Guid.TryParse(s.ETag, out var g) ? libraryManager.GetItemById(g) ?? fallback : fallback;
    }

    private static bool IsGelatoPlaybackItem(BaseItem item) =>
        item.HasStreamTag()
        || (item.Path?.StartsWith("gelato://", StringComparison.OrdinalIgnoreCase) ?? false);

    public async Task<MediaSourceInfo> GetMediaSource(
        BaseItem item,
        string mediaSourceId,
        string? liveStreamId,
        bool enablePathSubstitution,
        CancellationToken cancellationToken
    )
    {
        var source = await _inner
            .GetMediaSource(
                item,
                mediaSourceId,
                liveStreamId,
                enablePathSubstitution,
                cancellationToken
            )
            .ConfigureAwait(false);

        if (
            source is null
            && item.GetBaseItemKind() is (BaseItemKind.Movie or BaseItemKind.Episode)
        )
        {
            var sources = GetStaticMediaSources(item, enablePathSubstitution, user: null);
            source = sources.FirstOrDefault(s =>
                    string.Equals(s.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase)
                ) ?? sources.FirstOrDefault(s =>
                    string.Equals(s.ETag, mediaSourceId, StringComparison.OrdinalIgnoreCase)
                );
        }

        return source;
    }

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
            Container = !string.IsNullOrWhiteSpace(item.Container)
                ? item.Container
                : "hls",
            Size = item.Size,
            Type = type,
            SupportsDirectStream = true,
            // Gelato streams are proxied through Jellyfin: the source Path is stubbed to
            // "/stub" (File protocol) so clients never hit the remote URL directly. That
            // makes DirectPlay impossible and absurd to advertise. On Jellyfin 12 the web
            // player honors SupportsDirectPlay=true and tries to direct-play the stub,
            // failing before it ever requests the (functioning) remux. Force proxying.
            SupportsDirectPlay = false,
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
                info.Path = string.IsNullOrWhiteSpace(video.ShortcutPath)
                    ? video.Path
                    : video.ShortcutPath;
            }
        }

        info.Bitrate = item.TotalBitrate;
        info.InferTotalBitrate();

        return info;
    }

    private static readonly HashSet<string> _subtitleExtensions = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "vtt",
        "srt",
        "ass",
        "ssa",
        "sub",
        "idx",
        "smi",
    };

    // Jellyfin's MediaInfoResolver.GetExternalStreamsAsync bails immediately when !video.IsFileProtocol
    // (stream items have http:// paths). This means external subtitle files saved to the internal
    // metadata folder are never discovered during library refresh and never written to the DB.
    // We work around this by scanning the metadata folder ourselves at playback time and merging
    // any matching subtitle files into the DB streams on the fly.
    private IReadOnlyList<MediaStream> GetMediaStreamsWithExternalSubs(BaseItem item)
    {
        var streams = _inner.GetMediaStreams(item.Id).ToList();

        var gelatoFilename = item.GelatoData<string>("filename");
        if (string.IsNullOrEmpty(gelatoFilename))
            return streams;

        var metaPath = item.GetInternalMetadataPath();
        if (!Directory.Exists(metaPath))
            return streams;

        var baseName = Path.GetFileNameWithoutExtension(gelatoFilename);
        var existingPaths = new HashSet<string>(
            streams.Where(s => s.Path != null).Select(s => s.Path!),
            StringComparer.OrdinalIgnoreCase
        );

        var nextIndex = streams.Count > 0 ? streams.Max(s => s.Index) + 1 : 0;

        foreach (var file in Directory.EnumerateFiles(metaPath))
        {
            var fname = Path.GetFileName(file);

            // Must start with baseName + "."
            if (!fname.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase))
                continue;

            var ext = Path.GetExtension(fname).TrimStart('.');
            if (!_subtitleExtensions.Contains(ext))
                continue;

            if (existingPaths.Contains(file))
                continue;

            // Parse language from suffix: {baseName}.{lang}.{ext} or {baseName}.{lang}.{N}.{ext}
            var suffix = fname.Substring(baseName.Length + 1); // everything after "baseName."
            var parts = Path.GetFileNameWithoutExtension(suffix).Split('.');
            var langCode = parts.Length > 0 ? parts[0] : "und";

            streams.Add(
                new MediaStream
                {
                    Type = MediaStreamType.Subtitle,
                    IsExternal = true,
                    IsExternalUrl = false,
                    SupportsExternalStream = true,
                    Path = file,
                    Language = langCode,
                    Codec = ext.ToLowerInvariant(),
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

}
