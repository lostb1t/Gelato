#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Controller.Persistence;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.IO;
using Jellyfin.Database.Implementations.Entities;
using Gelato.Common;

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

        public MediaSourceManagerDecorator(
            IMediaSourceManager inner,
              ILibraryManager libraryManager,
            ILogger<MediaSourceManagerDecorator> log,
            IHttpContextAccessor http,
              IDirectoryService directoryService,
        GelatoStremioProvider stremioProvider,
            Lazy<GelatoManager> manager

            )
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _log = log ?? throw new ArgumentNullException(nameof(log));
            _http = http ?? throw new ArgumentNullException(nameof(http)); _manager = manager;
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

            if ((!GelatoPlugin.Instance!.Configuration.EnableMixed && !manager.IsStremio(item)) ||
                item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode))
            {
                _log.LogDebug("GetStaticMediaSources not marked for streams");
                return _inner.GetStaticMediaSources(item, enablePathSubstitution, user);
            }

            var ctx = _http?.HttpContext;
            var uri = StremioUri.FromBaseItem(item);

            string? actionName = null;
            if (ctx?.Items.TryGetValue("actionName", out var actionObj) == true)
                actionName = actionObj as string;

            var isItemsAction = IsItemsActionName(actionName ?? string.Empty);
            var isListAction = IsList(actionName ?? string.Empty);

            var allowSync =
                isItemsAction &&
                (!isListAction || IsSingleItemList(ctx, item.Id));

            if (!allowSync)
            {
                _log.LogDebug("GetStaticMediaSources not a sync-eligible call. action={Action} list={IsList} uri={Uri}",
                    actionName, isListAction, uri?.ToString());
            }
            else if (uri is not null && !manager.HasStreamSync(item.Id))
            {
                _log.LogDebug("GetStaticMediaSources refreshing streams for {Id}", item.Id);

                manager.SyncStreams(item, CancellationToken.None).GetAwaiter().GetResult();
                manager.SetStreamSync(item.Id);
                
                var items = manager.FindByProviderIds(item.ProviderIds, item.GetBaseItemKind()).OfType<Video>().ToArray();

                if (items.Length > 1)
                    manager.MergeVersions(items).GetAwaiter().GetResult();

                item = _libraryManager.GetItemById(item.Id);
            }

            var sources = _inner.GetStaticMediaSources(item, enablePathSubstitution, user).ToList();

            sources = sources
                .Where(s => sources.Count <= 1 || s.Path == null || !s.Path.StartsWith("stremio", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.Protocol == MediaProtocol.File)
                .ThenBy(s => s.Name)
                .Select((s, idx) =>
                {
                    if (s.Protocol != MediaProtocol.File && !string.IsNullOrEmpty(s.Name))
                    {
                      s.Name = s.Name.Split(":::").Last();
                    }
                    return (s, idx);
                })
                .Select(t => t.s)
                .ToList();

            if (sources.Count > 0) sources[0].Type = MediaSourceType.Default;

            _log.LogDebug("GetStaticMediaSources finished for {Id} uri={Uri} action={Action}",
                item.Id, uri?.ToString(), actionName);

            return sources;
        }

        private static bool IsExternal(BaseItem item)
        {
            if (item.ProviderIds?.ContainsKey("stremio") == true) return true;
            if (!string.IsNullOrWhiteSpace(item.Path) &&
                item.Path.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public void AddParts(IEnumerable<IMediaSourceProvider> providers)
        {
            _providers = providers.ToArray();
            _inner.AddParts(providers);
        }

        // public void AddParts(IEnumerable<IMediaSourceProvider> providers) => _inner.AddParts(providers);

        public IReadOnlyList<MediaStream> GetMediaStreams(Guid itemId)
        {
            return _inner.GetMediaStreams(itemId);
        }

        public void AddSubtitleStreams(BaseItem item, MediaSourceInfo source)
        {

            //if (!IsExternal(item)) {
            //   return;
            // }

            var manager = _manager.Value;
            // var Id = item;

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

                streams.Add(new MediaStream
                {
                    Type = MediaStreamType.Subtitle,
                    Index = index++,
                    Language = s.Lang,
                    Codec = GuessSubtitleCodec(s.Url),               // "webvtt" / "subrip" / "ass"
                    IsExternal = true,
                    IsExternalUrl = true,
                    SupportsExternalStream = true,
                    Path = s.Url,
                    DeliveryMethod = SubtitleDeliveryMethod.External
                });
            }
            source.MediaStreams = streams;
            return;
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
            _log.LogWarning($"unkown subtitle format for {s}");
            return "subrip";
        }

        public IReadOnlyList<MediaStream> GetMediaStreams(MediaStreamQuery query)
        {
            return _inner.GetMediaStreams(query).ToList();

        }

        public IReadOnlyList<MediaAttachment> GetMediaAttachments(Guid itemId) => _inner.GetMediaAttachments(itemId);

        public IReadOnlyList<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) => _inner.GetMediaAttachments(query);

        public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
    BaseItem item,
    User user,
    bool allowMediaProbe,
    bool enablePathSubstitution,
    CancellationToken ct)
{
    if (item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode))
        return await _inner.GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, ct).ConfigureAwait(false);

    var manager = _manager.Value;
    var ctx = _http.HttpContext;

    static bool NeedsProbe(MediaSourceInfo s) =>
        (s.MediaStreams?.All(ms => ms.Type != MediaStreamType.Video) ?? true);

    BaseItem ResolveOwnerFor(MediaSourceInfo s, BaseItem fallback) =>
        Guid.TryParse(s.Id, out var g) ? (_libraryManager.GetItemById(g) ?? fallback) : fallback;

   // var sources = await _inner.GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, ct).ConfigureAwait(false);
    var sources = GetStaticMediaSources(item, enablePathSubstitution, user);
    string? mediaSourceId =
        ctx?.Items.TryGetValue("MediaSourceId", out var idObj) == true && idObj is string idStr ? idStr
        : manager.IsStremioPlaceholder(item) && sources.Count > 0 ? sources.First().Id
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
        //sources = await _inner.GetPlaybackMediaSources(owner, user, allowMediaProbe, enablePathSubstitution, ct).ConfigureAwait(false);

        selected = !string.IsNullOrEmpty(selected.Id)
            ? sources.FirstOrDefault(s => string.Equals(s.Id, selected.Id, StringComparison.OrdinalIgnoreCase)) ?? sources.FirstOrDefault()
            : sources.FirstOrDefault();
    }

    if (selected is null)
        return sources;

       // _log.LogDebug("Togo {ItemId} ={MediaSourceId}", allowMediaProbe, NeedsProbe(selected));
    
    if (NeedsProbe(selected))
    {
        await owner.RefreshMetadata(
            new MetadataRefreshOptions(_directoryService)
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh
            },
            ct).ConfigureAwait(false);

        var refreshed = GetStaticMediaSources(owner, enablePathSubstitution, user);
        selected = !string.IsNullOrEmpty(selected.Id)
            ? refreshed.FirstOrDefault(s => string.Equals(s.Id, selected.Id, StringComparison.OrdinalIgnoreCase)) ?? refreshed.FirstOrDefault()
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

    return selected is null ? Array.Empty<MediaSourceInfo>() : new[] { selected };
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

        public ILiveStream GetLiveStreamInfo(string id) => _inner.GetLiveStreamInfo(id);

        public ILiveStream GetLiveStreamInfoByUniqueId(string uniqueId) => _inner.GetLiveStreamInfoByUniqueId(uniqueId);

        public async Task<IReadOnlyList<MediaSourceInfo>> GetRecordingStreamMediaSources(
            ActiveRecordingInfo info,
            CancellationToken cancellationToken)
            => await _inner.GetRecordingStreamMediaSources(info, cancellationToken);

        public Task CloseLiveStream(string id) => _inner.CloseLiveStream(id);

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