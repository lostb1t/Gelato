using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalMedia.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExternalMedia;

public sealed class ExternalMediaSourceProvider : IMediaSourceProvider

{
    // A public test HLS; swap with your own.
    private const string TestHls = "https://test-videos.co.uk/vids/bigbuckbunny/mp4/h264/1080/Big_Buck_Bunny_1080_10s_30MB.mp4";
    private readonly ILogger<ExternalMediaSourceProvider> _log;
    private readonly ExternalMediaStremioProvider _stremioProvider;

    public ExternalMediaSourceProvider(ILogger<ExternalMediaSourceProvider> log, ExternalMediaStremioProvider stremioProvider)
    {

        _log = log;
        _stremioProvider = stremioProvider;

    }

    // this needs probe.
    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        var sources = await GetMediaSourcesNoProbe(item, cancellationToken);
        return sources;
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSourcesNoProbe(BaseItem item, CancellationToken cancellationToken)
    {
        _log.LogInformation("GetMediaSources called for item {ItemId} ({ItemName})", item.Id, item.Name);
        // if (!Matches(item))
        //     return Task.FromResult<IEnumerable<MediaSourceInfo>>(Array.Empty<MediaSourceInfo>());
        var stremioKey = item.GetProviderId("stremio");
        var streams = await _stremioProvider.GetStreamsAsync(item).ConfigureAwait(false);

        var sources = new List<MediaSourceInfo>();
        foreach (var stream in streams)
        {
            var streamId = stream.GetGuid().ToString();
            var source = new MediaSourceInfo
            {
                //Id = streams[0].GetGuid().ToString(),
                Id = $"{item.Id:N}::{streamId:N}",
                // Id = GuidCodec.EncodeString(StremioId.ToCompactId(stremioKey)).ToString(),
                // Name = "Basic Live HLS",
                Name = stream.GetName(),
                Path = stream.Url,
                Protocol = MediaProtocol.Http,
                // Container = "mp4",
                IsRemote = true,

                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = false,
                SupportsProbing = true,
                VideoType = VideoType.VideoFile,
                // CandWo
                // CanDownload = true,
                // CanPlay = true,

                // MediaStreams = new List<MediaStream>
                //     {
                //         new MediaStream { Type = MediaStreamType.Video, Codec = "h264", IsDefault = true },
                //         new MediaStream { Type = MediaStreamType.Audio, Codec = "aac",  IsDefault = true }
                //     }
            };
            sources.Add(source);
        }

        return sources;
    }

    private bool Matches(BaseItem item)
    {
        _log.LogInformation("Checking if item {ItemId} matches external media criteria", item.Id);
        // Trigger #1: ProviderId gate (easy to set from your code)
        if (item.ProviderIds != null && item.ProviderIds.TryGetValue("LiveTest", out var _))
            return true;

        // Trigger #2: Path prefix (handy when you can control Path or use virtual items)
        if (!string.IsNullOrWhiteSpace(item.Path) && item.Path.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase))
            return true;

        return true;
    }


    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        _log.LogInformation("OpenMediaSource called with token {OpenToken}", openToken);
        throw new NotImplementedException();
    }
}
