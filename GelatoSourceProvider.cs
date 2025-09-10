using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Dlna;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Gelato.Common;
using MediaBrowser.Common.Configuration;

namespace Gelato;

public sealed class GelatoSourceProvider : IMediaSourceProvider
{
    private readonly ILogger<GelatoSourceProvider> _log;
    private readonly GelatoStremioProvider _stremioProvider;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IMemoryCache _cache;
    // private readonly IMediaStreamRepository _streamRepo;
    private readonly GelatoManager _manager;
    private readonly IFileSystem _fs;
    private readonly IHttpContextAccessor _http;
    private static readonly TimeSpan NoProbeTtl = TimeSpan.FromMinutes(3600);
    private readonly FileCache _fileCache;

    public GelatoSourceProvider(
        GelatoManager manager,
        //  IMediaStreamRepository streamRepo,
        ILogger<GelatoSourceProvider> log,
        GelatoStremioProvider stremioProvider,
        IMediaEncoder mediaEncoder,
        IMemoryCache cache,
        IHttpContextAccessor http,
        IApplicationPaths appPaths,
        IFileSystem fs)
    {
        _log = log;
        _manager = manager;
        //  _streamRepo = streamRepo;
        _stremioProvider = stremioProvider;
        _mediaEncoder = mediaEncoder;
        _cache = cache;
        _fs = fs;
        _http = http;

        var cacheDir = Path.Combine(appPaths.CachePath, "GelatoMediaInfo");
        _fileCache = new FileCache(cacheDir);
    }



    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(
    BaseItem item,
    bool allowMediaProbe,
    CancellationToken ct)
{
    //_log.LogInformation("getting sources");
    var sources = await GetMediaSourcesNoProbe(item, ct).ConfigureAwait(false);

    if (!allowMediaProbe)
        return sources;

    IEnumerable<MediaSourceInfo> selected = sources;

    var ctx = _http.HttpContext;
    if (ctx != null && ctx.Items.TryGetValue("MediaSourceId", out var idObj) && idObj is string mediaSourceId)
    {
        _log.LogInformation("Probing only selected media source {MediaSourceId}", mediaSourceId);
        selected = sources.Where(s => s.Id == mediaSourceId);
    }
    else if (sources.Any())
    {
        var first = sources.First();
        _log.LogInformation("No MediaSourceId provided, defaulting to first source {MediaSourceId}", first.Id);
        selected = new[] { first };
    }
    
  //  _log.LogInformation("getting probes");

    var tasks = selected.Select(src => ProbeAndPatchAsync(item, src, ct));
    await Task.WhenAll(tasks).ConfigureAwait(false);

    return selected;
}

    async Task<IEnumerable<MediaSourceInfo>> IMediaSourceProvider.GetMediaSources(BaseItem item, CancellationToken ct)
        => await GetMediaSources(item, allowMediaProbe: true, ct).ConfigureAwait(false);

    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSourcesNoProbe(BaseItem item, CancellationToken ct)
    {
        var cacheKey = $"mediaSource:{item.Id:N}";

        var list = await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = NoProbeTtl;

            var streams = await _stremioProvider.GetStreamsAsync(item).ConfigureAwait(false);

            var built = new List<MediaSourceInfo>();
            foreach (var stream in streams)
            {
                if (!stream.IsValid())
                    continue;

                // _log.LogInformation("Found stream {Name} ({Url})", stream.GetName(), stream.Url);

                var streamGuid = stream.GetGuid();
                var stremioBaseUri = item.GetProviderId("stremio");
                var uri = StremioUri.FromString($"{stremioBaseUri:N}/{streamGuid}");
                _manager.SaveStremioUri(streamGuid, uri);

                built.Add(new MediaSourceInfo
                {
                    Id = streamGuid.ToString(),
                    Name = stream.GetName(),
                    Path = stream.Url,
                    Protocol = MediaProtocol.Http,
                    IsRemote = true,
                    SupportsDirectPlay = true,
                    SupportsDirectStream = true,
                    SupportsTranscoding = false,
                    SupportsProbing = true,
                    VideoType = VideoType.VideoFile,
                    // Container = GuessContainerFromUrl(stream.Url)
                });
            }

            return built;
        }).ConfigureAwait(false);

        return list;
    }

    private async Task ProbeAndPatchAsync(BaseItem item, MediaSourceInfo mediaSource, CancellationToken ct)
    {
        try
        {

            var mediaInfo = await _fileCache.GetAsync<MediaInfo>(mediaSource.Id, ct);

            if (mediaInfo is null)
            {
                mediaInfo = await _mediaEncoder.GetMediaInfo(
                    new MediaInfoRequest
                    {
                        MediaType = DlnaProfileType.Video,
                        MediaSource = mediaSource
                    },
                    ct);
                await _fileCache.SetAsync(mediaSource.Id, mediaInfo, ct);
            }

            mediaSource.Bitrate = mediaInfo.Bitrate;
            mediaSource.Container = mediaInfo.Container;
            mediaSource.Formats = mediaInfo.Formats;
            mediaSource.MediaStreams = mediaInfo.MediaStreams;
            mediaSource.RunTimeTicks = mediaInfo.RunTimeTicks;
            mediaSource.Size = mediaInfo.Size;
            mediaSource.Timestamp = mediaInfo.Timestamp;
            mediaSource.Video3DFormat = mediaInfo.Video3DFormat;
            mediaSource.VideoType = mediaInfo.VideoType;

            _log.LogDebug(
                "Probed {Id}: container={Container}, bitrate={Bitrate}bps, streams={Streams}",
                mediaSource.Id,
                mediaSource.Container ?? "(unknown)",
                mediaSource.Bitrate.GetValueOrDefault(),
                mediaSource.MediaStreams?.Count ?? 0
            );
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Probe failed for source {Id} ({Path})", mediaSource.Id, mediaSource.Path);
        }
    }


    // private static string? GuessContainerFromUrl(string? url)
    // {
    //     if (string.IsNullOrWhiteSpace(url)) return null;
    //     if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)) return "m3u8";
    //     if (url.Contains(".mpd", StringComparison.OrdinalIgnoreCase)) return "mpd";
    //     if (url.Contains(".mp4", StringComparison.OrdinalIgnoreCase)) return "mp4";
    //     if (url.Contains(".m4s", StringComparison.OrdinalIgnoreCase)) return "mp4";
    //     if (url.Contains(".mkv", StringComparison.OrdinalIgnoreCase)) return "mkv";
    //     return null;
    // }

    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        _log.LogInformation("OpenMediaSource called with token {OpenToken}", openToken);
        throw new NotImplementedException();
    }



}