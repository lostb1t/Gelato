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
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.IO;        

namespace Gelato;

public sealed class MediaSourceManagerDecorator : IMediaSourceManager
{
    private readonly IMediaSourceManager _inner;
    private readonly ILogger<MediaSourceManagerDecorator> _log;
    private readonly IHttpContextAccessor _http;
private readonly ILibraryManager _libraryManager;
    private readonly Lazy<GelatoManager> _manager;
    private IMediaSourceProvider[] _providers;
        //private readonly GelatoStremioProvider _stremioProvider;
private readonly IDirectoryService _directoryService;
    public MediaSourceManagerDecorator(
        IMediaSourceManager inner,
          ILibraryManager libraryManager,
        ILogger<MediaSourceManagerDecorator> log,
        IHttpContextAccessor http,
          IDirectoryService directoryService,
            //    GelatoStremioProvider stremioProvider,  
        Lazy<GelatoManager> manager
        // GelatoSourceProvider externalMediaSourceProvider
        )
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        // _externalMediaSourceProvider = externalMediaSourceProvider ?? throw new ArgumentNullException(nameof(externalMediaSourceProvider));
        _manager = manager;
         _libraryManager = libraryManager;
        _directoryService = directoryService;
             //   _stremioProvider = stremioProvider;
    }
    
  public bool IsItemsActionName(string name)
    {
        return string.Equals(name, "GetItems", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "GetItem", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "GetItemLegacy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "GetItemsByUserIdLegacy", StringComparison.OrdinalIgnoreCase);
    }  
//        private GelatoSourceProvider? GetGelatoSourceProvider()
//{
//    return _providers
//        .OfType<GelatoSourceProvider>()
//        .FirstOrDefault();
//}

    /// lots of episodes rewauwst episodes including streams. Have to fix that before using thisp 
    public List<MediaSourceInfo> GetStaticMediaSources(
    BaseItem item, 
    bool enablePathSubstitution, 
    User user = null)
{
  var manager = _manager.Value;
var ctx = _http.HttpContext;
    if (IsExternal(item) && ctx != null && ctx.Items.TryGetValue("actionName", out var actionName) && IsItemsActionName(actionName as string))
    {
      _log.LogInformation("huhhhsa");
     
      if (!manager.HasStreamSync(item.Id)) {
     manager.SyncStreams(item, CancellationToken.None).GetAwaiter().GetResult();
     manager.SetStreamSync(item.Id);
     _libraryManager.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None).GetAwaiter().GetResult();
   }
    }
    
    var sources = _inner.GetStaticMediaSources(item, enablePathSubstitution, user);
    var sorted = sources
    .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
    .Select(s =>
    {

        if (s.Name.Length > 4)
            s.Name = s.Name.Substring(4);
        return s;
    })
    .Where(s => s.Path == null ||
                !s.Path.StartsWith("stremio", StringComparison.OrdinalIgnoreCase))
    .ToList();

return sorted;
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
       // if (!IsExternal(item))
    //   _log.LogInformation("playbackinfo calked  {MediaSourceId}", item.Id); 
      var ctx = _http.HttpContext;
      
      if (ctx?.Items.TryGetValue("MediaSourceId", out var idObjj) == true)
{
    _log.LogInformation("got id {MediaSourceId}", idObjj);
}
      
    if (ctx != null && ctx.Items.TryGetValue("MediaSourceId", out var idObj) && idObj is string mediaSourceId)
    {
          //var mediaSources = GetStaticMediaSources(item, enablePathSubstitution, user);
                 //     _log.LogInformation("got iddddd {MediaSourceId}", mediaSourceId); 
        //selected = sources.Where(s => s.Id == mediaSourceId);
        var sourceItem = _libraryManager.GetItemById(Guid.Parse(mediaSourceId));
        var mediaSources = GetStaticMediaSources(sourceItem, enablePathSubstitution, user);
        if (allowMediaProbe && !sourceItem.Path.StartsWith("stremio:", StringComparison.OrdinalIgnoreCase)
                    && (sourceItem.MediaType == MediaType.Video && mediaSources[0].MediaStreams.All(i => i.Type != MediaStreamType.Video)))
                   // || (item.MediaType == MediaType.Audio && mediaSources[0].MediaStreams.All(i => i.Type != MediaStreamType.Audio))))
            {
                     // _log.LogInformation("Probing only selected media source {MediaSourceId}", mediaSourceId); 
              await sourceItem.RefreshMetadata(
                    new MetadataRefreshOptions(_directoryService)
                    {
                        EnableRemoteContentProbe = true,
                        MetadataRefreshMode = MetadataRefreshMode.FullRefresh
                    },
                    cancellationToken).ConfigureAwait(false);

                //mediaSources = GetStaticMediaSources(item, enablePathSubstitution, user);
            }
    }
    
      
        return await _inner.GetPlaybackMediaSources(item, user, false, enablePathSubstitution, cancellationToken);     
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