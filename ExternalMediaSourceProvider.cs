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
using MediaBrowser.Controller.Providers;   // MetadataRefreshOptions, DirectoryService, ImageRefreshMode
using MediaBrowser.Model.Providers;        // IDirectoryService interface
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
//using MediaBrowser.Providers.MediaInfo;
using MediaBrowser.Model.Dlna;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.ExternalMedia.Common;

namespace Jellyfin.Plugin.ExternalMedia;

public sealed class ExternalMediaSourceProvider : IMediaSourceProvider
{
    private readonly ILogger<ExternalMediaSourceProvider> _log;
    private readonly ExternalMediaStremioProvider _stremioProvider;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IMemoryCache _cache;
    private readonly IMediaStreamRepository _streamRepo;
    // private readonly FFProbeVideoInfo _ffprobe;
    private readonly IFileSystem _fs;

    private static readonly TimeSpan NoProbeTtl = TimeSpan.FromMinutes(3600);

    public ExternalMediaSourceProvider(
        //   FFProbeVideoInfo ffprobe,
        IMediaStreamRepository streamRepo,
        ILogger<ExternalMediaSourceProvider> log,
        ExternalMediaStremioProvider stremioProvider,
        IMediaEncoder mediaEncoder,
        IMemoryCache cache,
        IFileSystem fs)
    {
        _log = log;
        //  _ffprobe = ffprobe;
        _streamRepo = streamRepo;
        _stremioProvider = stremioProvider;
        _mediaEncoder = mediaEncoder;
        _cache = cache;
        _fs = fs;
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, bool allowMediaProbe, CancellationToken ct)
    {
        var sources = (await GetMediaSourcesNoProbe(item, ct).ConfigureAwait(false)).ToList();
        if (!allowMediaProbe || sources.Count == 0)
            return sources;

        var tasks = sources.Select(src => ProbeAndPatchAsync(item, src, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return sources;
    }

    async Task<IEnumerable<MediaSourceInfo>> IMediaSourceProvider.GetMediaSources(BaseItem item, CancellationToken ct)
        => await GetMediaSources(item, allowMediaProbe: true, ct).ConfigureAwait(false);

    /// <summary>
    /// Build sources using Stremio (cached). Does NOT probe and does NOT apply any probe cache.
    /// </summary>
    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSourcesNoProbe(BaseItem item, CancellationToken ct)
    {
        var cacheKey = $"noprobe:{item.Id:N}";

        var list = await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = NoProbeTtl;

            // _log.LogInformation("No-probe cache MISS for item {ItemId} ({Name})", item.Id, item.Name);

            var streams = await _stremioProvider.GetStreamsAsync(item).ConfigureAwait(false);

            var built = new List<MediaSourceInfo>();
            foreach (var stream in streams)
            {
                if (!stream.IsValid())
                    continue;

                var streamId = stream.GetShortId();
                var stremioBaseUri = item.GetProviderId("stremio");
                var uri = StremioUri.LoadFromString($"{stremioBaseUri:N}/{streamId}");
                // var streamOd = $"{item.Id:N}::{strea}",

                built.Add(new MediaSourceInfo
                {
                    Id = uri.ToGuidEncoded().ToString("N"),
                    Name = stream.GetName(),
                    Path = stream.Url,
                    Protocol = MediaProtocol.Http,
                    IsRemote = true,
                    SupportsDirectPlay = true,
                    SupportsDirectStream = true,
                    SupportsTranscoding = false,
                    SupportsProbing = true,
                    VideoType = VideoType.VideoFile,
                    Container = GuessContainerFromUrl(stream.Url)
                });
            }

            return built;
        }).ConfigureAwait(false);

        // IMPORTANT: no probe snapshot application here (cache removed by request)
        return list;
    }

    // ---------------------------
    // Probing
    // ---------------------------

    private async Task ProbeAndPatchAsync(BaseItem item, MediaSourceInfo src, CancellationToken ct)
    {


        try
        {
            // We want to probe the actual stream URL in 'src', not necessarily item.Path.
            // Create a lightweight temporary Video to point at this URL so ProbeVideo uses it.
            var temp = new Movie
            {
                Name = item.Name,
                Path = src.Path
            };

            var options = new MetadataRefreshOptions(new DirectoryService(_fs))
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                //ImageRefreshMode = ImageRefreshMode.None
            };

            var info = await _mediaEncoder.GetMediaInfo(
                new MediaInfoRequest
                {
                    MediaType = DlnaProfileType.Video,
                    MediaSource = new MediaSourceInfo
                    {
                        Path = src.Path,
                        Protocol = MediaProtocol.Http
                    }
                },
                ct);

            //_log.LogInformation("Probing via FFProbeVideoInfo for {Id} ({Path})", src.Id, src.Path);
            // await _ffprobe.ProbeVideo(temp, options, ct).ConfigureAwait(false);

            // After ProbeVideo, extract concrete technicals by asking encoder for this exact URL.


            PatchFromMediaInfo(src, info);
            // _streamRepo.SaveMediaStreams(src.Id, info.MediaStreams, ct);
            _log.LogInformation(
                "Probed {Id}: container={Container}, bitrate={Bitrate}bps, streams={Streams}",
                src.Id,
                src.Container ?? "(unknown)",
                src.Bitrate.GetValueOrDefault(),
                src.MediaStreams?.Count ?? 0
            );
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Probe failed for source {Id} ({Path})", src.Id, src.Path);
        }
    }

    private static void PatchFromMediaInfo(MediaSourceInfo target, MediaInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.Container))
            target.Container = info.Container;

        if (info.Bitrate.HasValue && info.Bitrate.Value > 0)
            target.Bitrate = info.Bitrate;

        if (info.RunTimeTicks.HasValue && info.RunTimeTicks.Value > 0)
            target.RunTimeTicks = info.RunTimeTicks;

        if (info.MediaStreams is { Count: > 0 })
            target.MediaStreams = info.MediaStreams.Select(CloneStream).ToList();

        target.SupportsProbing = false;
        target.SupportsTranscoding = true;
    }

    // ---------------------------
    // Helpers
    // ---------------------------

    private static MediaStream CloneStream(MediaStream s) => new()
    {
        Type = s.Type,
        Codec = s.Codec,
        Width = s.Width,
        Height = s.Height,
        Channels = s.Channels,
        BitRate = s.BitRate,
        IsDefault = s.IsDefault,
        IsForced = s.IsForced,
        Index = s.Index,
        IsExternal = s.IsExternal,
        SupportsExternalStream = s.SupportsExternalStream,
        ColorSpace = s.ColorSpace,
        ColorTransfer = s.ColorTransfer,
        ColorPrimaries = s.ColorPrimaries,


        NalLengthSize = s.NalLengthSize,
        IsInterlaced = s.IsInterlaced,
        IsAVC = s.IsAVC,
        TimeBase = s.TimeBase,

        SampleRate = s.SampleRate,
        Language = s.Language
    };

    private static string? GuessContainerFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)) return "m3u8";
        if (url.Contains(".mpd", StringComparison.OrdinalIgnoreCase)) return "mpd";
        if (url.Contains(".mp4", StringComparison.OrdinalIgnoreCase)) return "mp4";
        if (url.Contains(".m4s", StringComparison.OrdinalIgnoreCase)) return "mp4";
        if (url.Contains(".mkv", StringComparison.OrdinalIgnoreCase)) return "mkv";
        return null;
    }

    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        _log.LogInformation("OpenMediaSource called with token {OpenToken}", openToken);
        throw new NotImplementedException();
    }
}