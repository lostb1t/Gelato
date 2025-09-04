using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExternalMedia;

public sealed class ExternalMediaStreamProvider : IMediaSourceProvider
{
    // A public test HLS; swap with your own.
    private const string TestHls = "https://test-streams.mux.dev/x36xhzz/x36xhzz.m3u8";
    private readonly ILogger<ExternalMediaStreamProvider> _log;

    public ExternalMediaStreamProvider(ILogger<ExternalMediaStreamProvider> log)
    {

        _log = log;

    }


    public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        _log.LogInformation("GetMediaSources called for item {ItemId} ({ItemName})", item.Id, item.Name);
        if (!Matches(item))
            return Task.FromResult<IEnumerable<MediaSourceInfo>>(Array.Empty<MediaSourceInfo>());

        var source = new MediaSourceInfo
        {
            Id = item.Id.ToString("N") + "_live",
            Name = "Basic Live HLS",
            Path = TestHls,
            Protocol = MediaProtocol.Http,
            Container = "m3u8",
            IsRemote = true,

            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = false,

            MediaStreams = new List<MediaStream>
                {
                    new MediaStream { Type = MediaStreamType.Video, Codec = "h264", IsDefault = true },
                    new MediaStream { Type = MediaStreamType.Audio, Codec = "aac",  IsDefault = true }
                }
        };

        return Task.FromResult<IEnumerable<MediaSourceInfo>>(new[] { source });
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
        throw new NotImplementedException();
    }
}
