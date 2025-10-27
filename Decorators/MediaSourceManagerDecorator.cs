#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelato.Common;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
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
        private readonly Lazy<GelatoManager> _manager;
        private IMediaSourceProvider[] _providers;
        private readonly IDirectoryService _directoryService;
        private readonly GelatoStremioProvider _stremio;
        private readonly KeyLock _lock = new();

        public MediaSourceManagerDecorator(
            IMediaSourceManager inner,
            ILibraryManager libraryManager,
            ILogger<MediaSourceManagerDecorator> log,
            IHttpContextAccessor http,
            IDirectoryService directoryService,
            GelatoStremioProvider stremioProvider,
            Lazy<GelatoManager> manager)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _manager = manager;
            _libraryManager = libraryManager;
            _directoryService = directoryService;
            _stremio = stremioProvider;
        }

        private static bool IsSingleItemList(HttpContext? ctx, Guid expectedId)
        {
            if (ctx?.Request?.Query is null) return false;

            var q = ctx.Request.Query;
            if (!q.TryGetValue("ids", out var idsRaw)) return false;

            var ids = idsRaw
                .SelectMany(v => v.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .ToArray();

            return ids.Length == 1;
        }

        public bool IsItemsActionName(string name)
        {
            return string.Equals(name, "GetItems", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "GetItem", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "GetItemLegacy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "GetVideoStream", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "GetSubtitleWithTicks", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "GetItemsByUserIdLegacy", StringComparison.OrdinalIgnoreCase);
        }

        public bool IsList(string name)
        {
            return string.Equals(name, "GetItems", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "GetItemsByUserIdLegacy", StringComparison.OrdinalIgnoreCase);
        }

        public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(
            BaseItem item,
            bool enablePathSubstitution,
            User user = null)
        {
            var manager = _manager.Value;

            _log.LogDebug("GetStaticMediaSources {Id}", item.Id);

            if (item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode))
            {
                return _inner.GetStaticMediaSources(item, enablePathSubstitution, user);
            }

            // A file can be added after Stremio has been inserted. If that's the case we just filter out Stremio.
            if (!GelatoPlugin.Instance!.Configuration.EnableMixed)
            {
                var srcs = _inner.GetStaticMediaSources(item, enablePathSubstitution, user);
                var hasFile = srcs.Any(v => v.Protocol == MediaProtocol.File);
                if (hasFile)
                {
                    return srcs.Where(v => v.Protocol == MediaProtocol.File).ToArray();
                }
            }

            var ctx = _http?.HttpContext;
            var uri = StremioUri.FromBaseItem(item);
            var actionName = ctx?.Items.TryGetValue("actionName", out var ao) == true ? ao as string : null;
            var isItemsAction = IsItemsActionName(actionName ?? string.Empty);
            var isListAction = IsList(actionName ?? string.Empty);

            var allowSync = isItemsAction && (!isListAction || IsSingleItemList(ctx, item.Id));

            var video = item as Video;
            Guid cacheKey = Guid.TryParse(video?.PrimaryVersionId, out var id) ? id : item.Id;

            if (!allowSync)
            {
                _log.LogDebug(
                    "GetStaticMediaSources not a sync-eligible call. action={Action} list={IsList} uri={Uri}",
                    actionName,
                    isListAction,
                    uri?.ToString());
            }
            else if (uri is not null && !manager.HasStreamSync(cacheKey))
            {
                // Bug in web UI that calls the detail page twice. So that's why there's a lock.
                _lock.RunSingleFlightAsync(item.Id, async ct =>
                {
                    _log.LogDebug("GetStaticMediaSources refreshing streams for {Id}", item.Id);
                    await manager.SyncStreams(item, ct).ConfigureAwait(false);
                    manager.SetStreamSync(cacheKey);
                }).GetAwaiter().GetResult();

                item = _libraryManager.GetItemById(item.Id);
            }

            var sources = _inner.GetStaticMediaSources(item, enablePathSubstitution, user).ToList();

            sources = sources
                .OrderByDescending(s => s.Protocol == MediaProtocol.File)
                .ThenBy(s => s.Container.Split(":::").First())
                .Select(s => {
                  s.Name = s.Container.Split(":::").Last();
                  s.Container = "";
                  return s;
                })
                .ToList();

            if (sources.Count > 0)
            {
                sources[0].Type = MediaSourceType.Default;
            }

            _log.LogDebug(
                "GetStaticMediaSources finished for {Id} uri={Uri} action={Action} count={Count}",
                item.Id,
                uri?.ToString(),
                actionName,
                sources.Count());

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

        public void AddSubtitleStreams(BaseItem item, MediaSourceInfo source)
        {
            var manager = _manager.Value;

            var subtitles = manager.GetStremioSubtitlesCache(item.Id);
            if (subtitles is null)
            {
                var uri = StremioUri.FromBaseItem(item);
                if (uri is null)
                {
                    _log.LogError($"unable to build stremio uri for {item.Name}");
                    return;
                }

                subtitles = _stremio.GetSubtitlesAsync(uri, null).GetAwaiter().GetResult();
                manager.SetStremioSubtitlesCache(item.Id, subtitles);
            }

            var streams = source.MediaStreams?.ToList() ?? new List<MediaStream>();
            var index = streams.Last().Index;

            foreach (var s in subtitles)
            {
                index++;
                streams.Add(new MediaStream
                {
                    Type = MediaStreamType.Subtitle,
                    Index = index,
                    Language = s.Lang,
                    Codec = GuessSubtitleCodec(s.Url),
                    IsExternal = true,
                    SupportsExternalStream = true,
                    Path = s.Url,
                    DeliveryMethod = SubtitleDeliveryMethod.External
                });
            }

            _log.LogDebug($"AddSubtitleStreams: loaded {streams.Count()} subtitles");
            source.MediaStreams = streams;
        }

        public string GuessSubtitleCodec(string? urlOrPath)
        {
            if (string.IsNullOrWhiteSpace(urlOrPath))
                return "subrip";

            var s = urlOrPath.ToLowerInvariant();

            if (s.Contains(".vtt")) return "vtt";
            if (s.Contains(".srt")) return "srt";
            if (s.Contains(".ass") || s.Contains(".ssa")) return "ass";
            if (s.Contains(".subf2m")) return "subrip";
            if (s.Contains("subs") && s.Contains(".strem.io"))
                return "srt"; // Stremio proxies are always normalized to .srt

            _log.LogWarning($"unkown subtitle format for {s}, defaulting to srt");
            return "srt";
        }

        public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query)
        {
            return _inner.GetMediaStreams(query).ToList();
        }

        public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId)
            => _inner.GetMediaAttachments(itemId);

        public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query)
            => _inner.GetMediaAttachments(query);

        public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
            BaseItem item,
            User user,
            bool allowMediaProbe,
            bool enablePathSubstitution,
            CancellationToken ct)
        {
            if (item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode))
                return await _inner.GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, ct)
                    .ConfigureAwait(false);

            var manager = _manager.Value;
            var ctx = _http.HttpContext;

            static bool NeedsProbe(MediaSourceInfo s) =>
                (s.MediaStreams?.All(ms => ms.Type != MediaStreamType.Video) ?? true);

            BaseItem ResolveOwnerFor(MediaSourceInfo s, BaseItem fallback) =>
                Guid.TryParse(s.Id, out var g) ? (_libraryManager.GetItemById(g) ?? fallback) : fallback;

            var sources = GetStaticMediaSources(item, enablePathSubstitution, user);

            string? mediaSourceId =
                ctx?.Items.TryGetValue("MediaSourceId", out var idObj) == true && idObj is string idStr
                    ? idStr
                    : manager.IsPrimaryVersion(item as Video) && sources.Count > 0
                        ? sources.First().Id
                        : null;

            _log.LogDebug("GetPlaybackMediaSources {ItemId} mediaSourceId={MediaSourceId}", item.Id, mediaSourceId);

            var selected =
                (!string.IsNullOrEmpty(mediaSourceId)
                    ? sources.FirstOrDefault(s => string.Equals(s.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase))
                    : sources.FirstOrDefault())
                ?? sources.FirstOrDefault();

            if (selected is null)
                return sources;

            var owner = ResolveOwnerFor(selected, item);
            if (owner.Id != item.Id)
            {
                sources = GetStaticMediaSources(owner, enablePathSubstitution, user);
                selected = !string.IsNullOrEmpty(selected.Id)
                    ? sources.FirstOrDefault(s => string.Equals(s.Id, selected.Id, StringComparison.OrdinalIgnoreCase))
                        ?? sources.FirstOrDefault()
                    : sources.FirstOrDefault();
            }

            if (selected is null)
                return sources;

            if (NeedsProbe(selected))
            {
                owner.IsVirtualItem = false;

                await owner.RefreshMetadata(
                        new MetadataRefreshOptions(_directoryService)
                        {
                            EnableRemoteContentProbe = true,
                            MetadataRefreshMode = MetadataRefreshMode.FullRefresh
                        },
                        ct)
                    .ConfigureAwait(false);

                var refreshed = GetStaticMediaSources(owner, enablePathSubstitution, user);

                selected = !string.IsNullOrEmpty(selected.Id)
                    ? refreshed.FirstOrDefault(s => string.Equals(s.Id, selected.Id, StringComparison.OrdinalIgnoreCase))
                        ?? refreshed.FirstOrDefault()
                    : refreshed.FirstOrDefault();

                if (selected is null)
                    return refreshed;
            }

            if (GelatoPlugin.Instance!.Configuration.EnableSubs)
                AddSubtitleStreams(item, selected);

            if (item.RunTimeTicks is null && selected?.RunTimeTicks is not null)
            {
                item.RunTimeTicks = selected.RunTimeTicks;
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
            }

            return new[] { selected };
        }

        public Task<MediaSourceInfo> GetMediaSource(
            BaseItem item,
            string mediaSourceId,
            string? liveStreamId,
            bool enablePathSubstitution,
            CancellationToken cancellationToken)
            => _inner.GetMediaSource(item, mediaSourceId, liveStreamId, enablePathSubstitution, cancellationToken);

        public async Task<LiveStreamResponse> OpenLiveStream(LiveStreamRequest request, CancellationToken cancellationToken)
            => await _inner.OpenLiveStream(request, cancellationToken);

        public async Task<Tuple<LiveStreamResponse, IDirectStreamProvider>> OpenLiveStreamInternal(
            LiveStreamRequest request,
            CancellationToken cancellationToken)
            => await _inner.OpenLiveStreamInternal(request, cancellationToken);

        public Task<MediaSourceInfo> GetLiveStream(string id, CancellationToken cancellationToken)
            => _inner.GetLiveStream(id, cancellationToken);

        public Task<Tuple<MediaSourceInfo, IDirectStreamProvider>> GetLiveStreamWithDirectStreamProvider(
            string id,
            CancellationToken cancellationToken)
            => _inner.GetLiveStreamWithDirectStreamProvider(id, cancellationToken);

        public ILiveStream GetLiveStreamInfo(string id)
            => _inner.GetLiveStreamInfo(id);

        public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId)
            => _inner.GetLiveStreamInfoByUniqueId(uniqueId);

        public async Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(
            ActiveRecordingInfo info,
            CancellationToken cancellationToken)
            => await _inner.GetRecordingStreamMediaSources(info, cancellationToken);

        public Task CloseLiveStream(string id)
            => _inner.CloseLiveStream(id);

        public async Task<MediaSourceInfo> GetLiveStreamMediaInfo(string id, CancellationToken cancellationToken)
            => await _inner.GetLiveStreamMediaInfo(id, cancellationToken);

        public bool SupportsDirectStream(string path, MediaProtocol protocol)
            => _inner.SupportsDirectStream(path, protocol);

        public MediaProtocol GetPathProtocol(string path)
            => _inner.GetPathProtocol(path);

        public void SetDefaultAudioAndSubtitleStreamIndices(BaseItem item, MediaSourceInfo source, User user)
            => _inner.SetDefaultAudioAndSubtitleStreamIndices(item, source, user);

        public Task AddMediaInfoWithProbe(
            MediaSourceInfo mediaSource,
            bool isAudio,
            string cacheKey,
            bool addProbeDelay,
            bool isLiveStream,
            CancellationToken cancellationToken)
            => _inner.AddMediaInfoWithProbe(mediaSource, isAudio, cacheKey, addProbeDelay, isLiveStream, cancellationToken);
    }
}