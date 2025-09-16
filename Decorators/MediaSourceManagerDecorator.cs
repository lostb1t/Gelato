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

        public bool IsItemsActionName(string name)
        {
            return string.Equals(name, "GetItems", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "GetItem", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "GetItemLegacy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "GetItemsByUserIdLegacy", StringComparison.OrdinalIgnoreCase);
        }

        public IReadOnlyList<MediaSourceInfo> GetStaticMediaSources(
        BaseItem item,
        bool enablePathSubstitution,
        User user = null)
        {
            if (!IsExternal(item))
                return _inner.GetStaticMediaSources(item, enablePathSubstitution, user);

            var manager = _manager.Value;
            var ctx = _http.HttpContext;
            if (ctx != null && ctx.Items.TryGetValue("actionName", out var actionName) && IsItemsActionName(actionName as string))
            {
                if (!manager.HasStreamSync(item.Id))
                {
                    manager.SyncStreams(item, CancellationToken.None).GetAwaiter().GetResult();
                    manager.SetStreamSync(item.Id);
                    // _libraryManager.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None).GetAwaiter().GetResult();
                }
            }

            var sources = _inner.GetStaticMediaSources(item, enablePathSubstitution, user);
            sources = sources
            // .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Where(s => sources.Count <= 1 ||
                        s.Path == null ||
                        !s.Path.StartsWith("stremio", StringComparison.OrdinalIgnoreCase))
            .ToList();
            sources[0].Type = MediaSourceType.Default;
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

         if (!IsExternal(item)) {
            return;
          }
        
         var manager = _manager.Value;
        // var Id = item;
         
         var subtitles = manager.GetStremioSubtitlesCache(item.Id);
         if (subtitles is null) {
          subtitles = _stremio.GetSubtitlesAsync(item, null).GetAwaiter().GetResult();
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
                    //Language = s.LangCode,
                      Language = s.Lang,
                 //   Title = s.Title,                  
                    Codec = GuessSubtitleCodec(s.Url),               // "webvtt" / "subrip" / "ass"
                    IsExternal = true,
                      IsExternalUrl = true,
                  //  IsTextSubtitleStream = true,
                    Path = s.Url,                            
                    DeliveryMethod = SubtitleDeliveryMethod.External
                   // IsForced = s.IsForced,
                   // Default = s.IsDefault
                });
         }
         source.MediaStreams = streams;
         return;
        }
        
        public static string GuessSubtitleCodec(string? path)
{
    if (string.IsNullOrWhiteSpace(path))
        return "subrip";

    var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    return ext switch
    {
        "srt"  => "subrip",    // SubRip (.srt)
        "vtt"  => "webvtt",    // WebVTT (.vtt)
        "ass"  => "ass",       // Advanced SubStation Alpha (.ass)
        _      => "subrip"     // Default when unknown
    };
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
            if (!IsExternal(item))
                return await _inner.GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, ct);

            var ctx = _http.HttpContext;

            static bool IsStremio(BaseItem? i) =>
               i?.Path != null && i.Path.StartsWith("stremio:", StringComparison.OrdinalIgnoreCase);

            static bool NeedsProbe(BaseItem? i, MediaSourceInfo s) =>
                i is not null
                && i.MediaType == MediaType.Video
              //  && !IsStremio(i)
                && (s.MediaStreams?.All(ms => ms.Type != MediaStreamType.Video) ?? true);

            MediaSourceInfo? PickFirst(BaseItem owner) =>
                GetStaticMediaSources(owner, enablePathSubstitution, user).FirstOrDefault();

            BaseItem ResolveOwnerFor(MediaSourceInfo s, BaseItem fallback)
                => Guid.TryParse(s.Id, out var g) ? (_libraryManager.GetItemById(g) ?? fallback) : fallback;

            string? mediaSourceId =
                ctx is not null && ctx.Items.TryGetValue("MediaSourceId", out var idObj) && idObj is string idStr
                    ? idStr : null;

            MediaSourceInfo? selected;
            BaseItem owner;

            if (!string.IsNullOrEmpty(mediaSourceId))
            {
                var ownerById = Guid.TryParse(mediaSourceId, out var gid) ? _libraryManager.GetItemById(gid) : null;
                owner = ownerById ?? item;
                var sources = GetStaticMediaSources(owner, enablePathSubstitution, user);
                selected = sources.FirstOrDefault(s => string.Equals(s.Id, mediaSourceId, StringComparison.OrdinalIgnoreCase))
                           ?? sources.FirstOrDefault();
            }
            else
            {
                owner = item;
                selected = PickFirst(owner);
            }

            if (selected is null)
                return await _inner.GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, ct).ConfigureAwait(false);

            var probeOwner = ResolveOwnerFor(selected, owner);

            if (allowMediaProbe && NeedsProbe(probeOwner, selected))
            {
                await probeOwner.RefreshMetadata(
                    new MetadataRefreshOptions(_directoryService)
                    {
                        EnableRemoteContentProbe = true,
                        MetadataRefreshMode = MetadataRefreshMode.FullRefresh
                    },
                    ct).ConfigureAwait(false);

                var refreshed = GetStaticMediaSources(probeOwner, enablePathSubstitution, user);
                selected = !string.IsNullOrEmpty(selected.Id)
                    ? refreshed.FirstOrDefault(s => string.Equals(s.Id, selected.Id, StringComparison.OrdinalIgnoreCase)) ?? refreshed.FirstOrDefault()
                    : refreshed.FirstOrDefault();
            }
            
         //   AddSubtitleStreams(item, selected);

            return selected is null ? new List<MediaSourceInfo>() : new List<MediaSourceInfo> { selected };
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