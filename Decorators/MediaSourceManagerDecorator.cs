#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelato.Common;
using Gelato.Configuration;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators
{
    public sealed class MediaSourceManagerDecorator : IMediaSourceManager
    {
        private readonly IMediaSourceManager _inner;
        private readonly ILogger<MediaSourceManagerDecorator> _log;
        private readonly IHttpContextAccessor _http;
        private readonly ILibraryManager _libraryManager;
        private readonly GelatoItemRepository _repo;
        private readonly Lazy<GelatoManager> _manager;
        private IMediaSourceProvider[] _providers;
        private readonly IDirectoryService _directoryService;
        private readonly KeyLock _lock = new();

        public MediaSourceManagerDecorator(
            IMediaSourceManager inner,
            ILibraryManager libraryManager,
            ILogger<MediaSourceManagerDecorator> log,
            IHttpContextAccessor http,
            GelatoItemRepository repo,
            IDirectoryService directoryService,
            Lazy<GelatoManager> manager
        )
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _manager = manager;
            _libraryManager = libraryManager;
            _directoryService = directoryService;
            _repo = repo;
        }

        public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(
            BaseItem item,
            bool enablePathSubstitution,
            User user = null
        )
        {
            var manager = _manager.Value;

            _log.LogDebug("GetStaticMediaSources {Id}", item.Id);
            var ctx = _http?.HttpContext;
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
                (!cfg.EnableMixed && !manager.IsGelato(item))
                || (item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode))
            )
            {
                return _inner.GetStaticMediaSources(item, enablePathSubstitution, user);
            }

            var uri = StremioUri.FromBaseItem(item);
            var actionName =
                ctx?.Items.TryGetValue("actionName", out var ao) == true ? ao as string : null;

            var allowSync = ctx.IsInsertableAction();

            var video = item as Video;
            string cacheKey = Guid.TryParse(video?.PrimaryVersionId, out var id)
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
                            _log.LogDebug(
                                "GetStaticMediaSources refreshing streams for {Id}",
                                item.Id
                            );
                            try
                            {
                                await manager.SyncStreams(item, userId, ct).ConfigureAwait(false);
                                manager.SetStreamSync(cacheKey);
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
                item = _libraryManager.GetItemById(item.Id);
            }

            var sources = _inner.GetStaticMediaSources(item, enablePathSubstitution, user).ToList();

            // we dont use jellyfins alternate versions crap. So we have to load it ourselves

            InternalItemsQuery query;

            if (item.GetBaseItemKind() == BaseItemKind.Episode)
            {
                var episode = (Episode)item;
                query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { item.GetBaseItemKind() },
                    ParentId = episode.SeasonId,
                    Recursive = false,
                    GroupByPresentationUniqueKey = false,
                    GroupBySeriesPresentationUniqueKey = false,
                    CollapseBoxSetItems = false,
                    IsDeadPerson = true,
                    IsVirtualItem = true,
                    IndexNumber = episode.IndexNumber,
                };
            }
            else
            {
                query = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { item.GetBaseItemKind() },
                    HasAnyProviderId = new() { { "Stremio", item.GetProviderId("Stremio") } },
                    Recursive = false,
                    GroupByPresentationUniqueKey = false,
                    GroupBySeriesPresentationUniqueKey = false,
                    CollapseBoxSetItems = false,
                    IsDeadPerson = true,
                    IsVirtualItem = true,
                };
            }

            var gelatoSources = _repo
                .GetItemList(query)
                .OfType<Video>()
                .Where(x => manager.IsGelato(x) && x.GelatoData("userId") == userId.ToString())
                .OrderBy(s => s.GelatoData("index"))
                .Select(s =>
                {
                    var k = GetVersionInfo(
                        enablePathSubstitution,
                        s,
                        MediaSourceType.Grouping,
                        ctx,
                        user
                    );
                    if (user is not null)
                    {
                        _inner.SetDefaultAudioAndSubtitleStreamIndices(item, k, user);
                    }
                    return k;
                })
                .ToList();

            sources.AddRange(gelatoSources);

            if (sources.Count > 1)
            {
                // remove primary from list when there are streams
                sources = sources
                    .Where(k => !k.Path.StartsWith("stremio", StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            // failsafe. mediasources cannot be null
            if (sources.Count == 0)
            {
                sources.Add(
                    GetVersionInfo(enablePathSubstitution, item, MediaSourceType.Default, ctx, user)
                );
            }

            if (sources.Count > 0)
                sources[0].Type = MediaSourceType.Default;

            //  if (mediaSourceId == item.Id)
            //  {
            // use primary id for first result. This is needed as some dlients dont listen to static media sources and ust use the primary id
            // yes this gives other issues. but im tired
            // sources[0].ETag = sources[0].Id;
            sources[0].Id = item.Id.ToString("N");
            //}

            return sources;
        }

        public void AddParts(IEnumerable<IMediaSourceProvider> providers)
        {
            _providers = providers.ToArray();
            _inner.AddParts(providers);
        }

        public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
        {
            return _inner.GetMediaStreams(itemId);
        }

        public async Task<List<MediaStream>> GetSubtitleStreams(
            BaseItem item,
            MediaSourceInfo source
        )
        {
            var manager = _manager.Value;

            var subtitles = manager.GetStremioSubtitlesCache(item.Id);
            if (subtitles is null)
            {
                var uri = StremioUri.FromBaseItem(item);
                if (uri is null)
                {
                    _log.LogError($"unable to build stremio uri for {item.Name}");
                    return new List<MediaStream>(); // Return empty list instead of void
                }

                Uri u = new Uri(source.Path);
                string filename = System.IO.Path.GetFileName(u.LocalPath);

                var cfg = GelatoPlugin.Instance!.GetConfig(Guid.Empty);
                subtitles = await cfg
                    .stremio.GetSubtitlesAsync(uri, filename)
                    .ConfigureAwait(false);
                manager.SetStremioSubtitlesCache(item.Id, subtitles);
            }

            var streams = new List<MediaStream>();

            if (subtitles == null || !subtitles.Any())
            {
                _log.LogDebug($"GetSubtitleStreams: no subtitles found");
                return streams;
            }

            var index = 0; // Start from 0 since this is a new list
            var limitedSubtitles = subtitles.GroupBy(s => s.Lang).SelectMany(g => g.Take(2));
            foreach (var s in limitedSubtitles)
            {
                streams.Add(
                    new MediaStream
                    {
                        Type = MediaStreamType.Subtitle,
                        Index = index,
                        Language = s.Lang,
                        Codec = GuessSubtitleCodec(s.Url),
                        IsExternal = true,
                        // subtitle urls usually dont end with an extension. Breaking some clients cause they fucking check the extension instead of thr codec field.
                        SupportsExternalStream = false,
                        Path = s.Url,
                        DeliveryMethod = SubtitleDeliveryMethod.External,
                    }
                );
                index++;
            }

            _log.LogDebug($"GetSubtitleStreams: loaded {streams.Count} subtitles");
            return streams;
        }

        public string GuessSubtitleCodec(string? urlOrPath)
        {
            if (string.IsNullOrWhiteSpace(urlOrPath))
                return "subrip";

            var s = urlOrPath.ToLowerInvariant();

            if (s.Contains(".vtt"))
                return "vtt";
            if (s.Contains(".srt"))
                return "srt";
            if (s.Contains(".ass") || s.Contains(".ssa"))
                return "ass";
            if (s.Contains(".subf2m"))
                return "subrip";
            if (s.Contains("subs") && s.Contains(".strem.io"))
                return "srt"; // Stremio proxies are always normalized to .srt

            _log.LogWarning($"unkown subtitle format for {s}, defaulting to srt");
            return "srt";
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
            var pathAndQuery =
                _http.HttpContext.Request.Path + _http.HttpContext.Request.QueryString;

            if (item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode))
            {
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

            var manager = _manager.Value;
            var ctx = _http.HttpContext;

            static bool NeedsProbe(MediaSourceInfo s) =>
                (s.MediaStreams?.All(ms => ms.Type != MediaStreamType.Video) ?? true)
                || (s.RunTimeTicks ?? 0) < TimeSpan.FromMinutes(2).Ticks;

            //BaseItem ResolveOwnerFor(MediaSourceInfo s, BaseItem fallback) =>
            //    Guid.TryParse(s.Id, out var g)
            //        ? (_libraryManager.GetItemById(g) ?? fallback)
            //        : fallback;

            BaseItem ResolveOwnerFor(MediaSourceInfo s, BaseItem fallback) =>
                Guid.TryParse(s.ETag, out var g)
                    ? (_libraryManager.GetItemById(g) ?? fallback)
                    : fallback;

            static MediaSourceInfo? SelectByIdOrFirst(IReadOnlyList<MediaSourceInfo> list, Guid? id)
            {
                if (!id.HasValue)
                    return list.FirstOrDefault();

                var target = id.Value;

                return list.FirstOrDefault(s =>
                        !string.IsNullOrEmpty(s.Id) && Guid.TryParse(s.Id, out var g) && g == target
                    ) ?? list.FirstOrDefault();
            }

            var sources = GetStaticMediaSources(item, enablePathSubstitution, user);

            Guid? mediaSourceId =
                ctx?.Items.TryGetValue("MediaSourceId", out var idObj) == true
                && idObj is string idStr
                && Guid.TryParse(idStr, out var fromCtx)
                    ? fromCtx
                    : (
                        manager.IsPrimaryVersion(item as Video)
                        && sources.Count > 0
                        && Guid.TryParse(sources[0].Id, out var fromSource)
                            ? fromSource
                            : (Guid?)null
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
            if (manager.IsPrimaryVersion(owner as Video) && owner.Id != item.Id)
            {
                sources = GetStaticMediaSources(owner, enablePathSubstitution, user);
                selected = SelectByIdOrFirst(sources, mediaSourceId);
                Console.Write("Not same");
                if (selected is null)
                    return sources;
            }

            if (NeedsProbe(selected))
            {
                var v = owner.IsVirtualItem;
                owner.IsVirtualItem = false;

                await owner
                    .RefreshMetadata(
                        new MetadataRefreshOptions(_directoryService)
                        {
                            EnableRemoteContentProbe = true,
                            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                        },
                        ct
                    )
                    .ConfigureAwait(false);

                owner.IsVirtualItem = v;

                await owner
                    .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                    .ConfigureAwait(false);

                var refreshed = GetStaticMediaSources(item, enablePathSubstitution, user);
                selected = SelectByIdOrFirst(refreshed, mediaSourceId);

                if (selected is null)
                    return refreshed;

                sources = refreshed;
            }

            if (GelatoPlugin.Instance!.Configuration.EnableSubs)
            {
                var subtitleStreams = await GetSubtitleStreams(item, selected)
                    .ConfigureAwait(false);

                var streams = selected.MediaStreams?.ToList() ?? new List<MediaStream>();

                var index = streams.LastOrDefault()?.Index ?? -1;
                foreach (var s in subtitleStreams)
                {
                    index++;
                    s.Index = index;
                    streams.Add(s);
                }

                selected.MediaStreams = streams;
            }

            if (item.RunTimeTicks is null && selected.RunTimeTicks is not null)
            {
                item.RunTimeTicks = selected.RunTimeTicks;
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                    .ConfigureAwait(false);
            }

            return new[] { selected };
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
            bool enablePathSubstitution,
            BaseItem item,
            MediaSourceType type,
            HttpContext ctx,
            User user = null
        )
        {
            ArgumentNullException.ThrowIfNull(item);

            var info = new MediaSourceInfo
            {
                Id = item.Id.ToString("N", CultureInfo.InvariantCulture),
                ETag = item.Id.ToString("N", CultureInfo.InvariantCulture),
                Protocol = MediaProtocol.Http,
                MediaStreams = _inner.GetMediaStreams(item.Id),
                MediaAttachments = _inner.GetMediaAttachments(item.Id),
                Name = item.GelatoData("name"),
                Path = item.Path,
                RunTimeTicks = item.RunTimeTicks,
                Container = item.Container,
                Size = item.Size,
                Type = type,
                SupportsDirectStream = true,
                SupportsDirectPlay = true,
            };

            if (user is not null)
            {
                info.SupportsTranscoding = user.HasPermission(
                    PermissionKind.EnableVideoPlaybackTranscoding
                );
                info.SupportsDirectStream = user.HasPermission(
                    PermissionKind.EnablePlaybackRemuxing
                );
            }
            if (string.IsNullOrEmpty(info.Path))
            {
                info.Type = MediaSourceType.Placeholder;
            }

            var video = item as Video;
            if (video is not null)
            {
                info.IsoType = video.IsoType;
                info.VideoType = video.VideoType;
                info.Video3DFormat = video.Video3DFormat;
                info.Timestamp = video.Timestamp;
                info.IsRemote = true;
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
    }
}
