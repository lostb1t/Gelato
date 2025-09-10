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
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Controller.Persistence;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;

namespace Gelato;

public sealed class MediaSourceManagerDecorator : IMediaSourceManager
{
    private readonly IMediaSourceManager _inner;
    private readonly ILogger<MediaSourceManagerDecorator> _log;
    private readonly IHttpContextAccessor _http;
    // private readonly GelatoSourceProvider _externalMediaSourceProvider;

    private IMediaSourceProvider[] _providers;

    public MediaSourceManagerDecorator(
        IMediaSourceManager inner,
        ILogger<MediaSourceManagerDecorator> log,
        IHttpContextAccessor http
        // GelatoSourceProvider externalMediaSourceProvider
        )
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        // _externalMediaSourceProvider = externalMediaSourceProvider ?? throw new ArgumentNullException(nameof(externalMediaSourceProvider));
    
    }
    
        private GelatoSourceProvider? GetGelatoSourceProvider()
{
    return _providers
        .OfType<GelatoSourceProvider>()
        .FirstOrDefault();
}

    /// lots of episodes rewauwst episodes including streams. Have to fix that before using thisp 
    public List<MediaSourceInfo> GetStaticMediaSources(
    BaseItem item, 
    bool enablePathSubstitution, 
    User user = null)
{
    //if (!IsExternal(item))
    return _inner.GetStaticMediaSources(item, enablePathSubstitution, user);

    //var provider = GetGelatoSourceProvider();
    //return provider
    //    .GetMediaSources(item, allowMediaProbe: false, ct: CancellationToken.None)
    //    .GetAwaiter()
    //    .GetResult().ToList();
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

    public List<MediaStream> GetMediaStreams(Guid itemId)
    {
      return _inner.GetMediaStreams(itemId);
    }

    public List<MediaStream> GetMediaStreams(MediaStreamQuery query) => _inner.GetMediaStreams(query);

    public List<MediaAttachment> GetMediaAttachments(Guid itemId) => _inner.GetMediaAttachments(itemId);

    public List<MediaAttachment> GetMediaAttachments(MediaAttachmentQuery query) => _inner.GetMediaAttachments(query);

    public async Task<List<MediaSourceInfo>> GetPlaybackMediaSources(
        BaseItem item,
        User user,
        bool allowMediaProbe,
        bool enablePathSubstitution,
        CancellationToken cancellationToken)
    {
        if (!IsExternal(item))
            await _inner.GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, cancellationToken);

        var sources = await GetGelatoSourceProvider().GetMediaSources(item, true, cancellationToken).ConfigureAwait(false);

        var list = new List<MediaSourceInfo>();

        foreach (var source in sources)
        {
            // Validate that this is actually possible
            if (source.SupportsDirectStream)
            {
                source.SupportsDirectStream = SupportsDirectStream(source.Path, source.Protocol);
            }

            if (user is not null)
            {
                SetDefaultAudioAndSubtitleStreamIndices(item, source, user);

                if (item.MediaType == MediaType.Audio)
                {
                    source.SupportsTranscoding = user.HasPermission(PermissionKind.EnableAudioPlaybackTranscoding);
                }
                else if (item.MediaType == MediaType.Video)
                {
                    source.SupportsTranscoding = user.HasPermission(PermissionKind.EnableVideoPlaybackTranscoding);
                    source.SupportsDirectStream = user.HasPermission(PermissionKind.EnablePlaybackRemuxing);
                }
            }

            list.Add(source);
        }

        return list;
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

    public async Task<List<MediaSourceInfo>> GetRecordingStreamMediaSources(
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