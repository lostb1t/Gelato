using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

public class MediaInfoDbClient(
    IHttpClientFactory http,
    ILogger<MediaInfoDbClient> log,
    string serverId
)
{
    private const string BaseUrl = "http://localhost:3000";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task SubmitAsync(
        Video owner,
        string? infoHash,
        int? fileIdx,
        IReadOnlyList<MediaStream> streams
    )
    {
        if (string.IsNullOrEmpty(infoHash))
        {
            log.LogDebug("MediaInfoDb: skipping {Id}, no infoHash", owner.Id);
            return;
        }

        var mediaId =
            owner.GetProviderId("Imdb") ?? owner.GetProviderId("Stremio") ?? owner.Id.ToString("N");

        var kind = owner.GetBaseItemKind() switch
        {
            Jellyfin.Data.Enums.BaseItemKind.Movie => "movie",
            Jellyfin.Data.Enums.BaseItemKind.Episode => "episode",
            _ => "unknown",
        };

        var payload = new MediaInfoPayload
        {
            MediaId = mediaId,
            Kind = kind,
            Filename = owner.GelatoData<string>("filename"),
            TorrentInfoHash = infoHash,
            TorrentFileIdx = fileIdx,
            Container = owner.Container,
            Size = owner.Size,
            Duration = owner.RunTimeTicks.HasValue
                ? (double)owner.RunTimeTicks.Value / TimeSpan.TicksPerSecond
                : null,
            Bitrate = owner.TotalBitrate,
            SourceId = serverId,
            SourceAppId = "gelato",
            Tracks = streams
                .Where(s =>
                    s.Type
                        is MediaStreamType.Video
                            or MediaStreamType.Audio
                            or MediaStreamType.Subtitle
                )
                .Select(MapTrack)
                .ToList(),
        };

        try
        {
            var json = JsonSerializer.Serialize(payload, JsonOpts);
            var client = http.CreateClient(nameof(MediaInfoDbClient));
            client.Timeout = TimeSpan.FromSeconds(10);

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var resp = await client
                .PostAsync($"{BaseUrl}/api/mediainfo", content)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                log.LogWarning("MediaInfoDb submit failed: {Status} {Body}", resp.StatusCode, body);
            }
            else
            {
                log.LogInformation("MediaInfoDb submit ok for {MediaId}", mediaId);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "MediaInfoDb submit error for {MediaId}", mediaId);
        }
    }

    private static TrackPayload MapTrack(MediaStream s) =>
        new()
        {
            Id = Guid.NewGuid(),
            Kind = s.Type switch
            {
                MediaStreamType.Video => "video",
                MediaStreamType.Audio => "audio",
                MediaStreamType.Subtitle => "subtitle",
                _ => "unknown",
            },
            Idx = s.Index,
            Codec = s.Codec,
            CodecTag = s.CodecTag,
            Language = s.Language,
            Title = s.Title ?? s.DisplayTitle,
            Profile = s.Profile,
            BitRate = s.BitRate,
            BitDepth = s.BitDepth,
            IsDefault = s.IsDefault,
            IsForced = s.IsForced,
            IsHearingImpaired = s.IsHearingImpaired,
            IsExternal = s.IsExternal,
            Width = s.Width,
            Height = s.Height,
            Fps = s.RealFrameRate.HasValue ? (double?)s.RealFrameRate.Value : null,
            AvgFps = s.AverageFrameRate.HasValue ? (double?)s.AverageFrameRate.Value : null,
            AspectRatio = s.AspectRatio,
            IsInterlaced = s.IsInterlaced,
            ColorRange = s.ColorRange,
            ColorSpace = s.ColorSpace,
            ColorTransfer = s.ColorTransfer,
            ColorPrimaries = s.ColorPrimaries,
            DvVersionMajor = s.DvVersionMajor,
            DvVersionMinor = s.DvVersionMinor,
            DvProfile = s.DvProfile,
            DvLevel = s.DvLevel,
            DvBlSignalCompatId = s.DvBlSignalCompatibilityId,
            Channels = s.Channels,
            ChannelLayout = s.ChannelLayout,
            SampleRate = s.SampleRate,
        };
}

sealed class MediaInfoPayload
{
    public required string MediaId { get; init; }
    public required string Kind { get; init; }
    public string? Filename { get; init; }
    public required string TorrentInfoHash { get; init; }
    public int? TorrentFileIdx { get; init; }
    public string? Container { get; init; }
    public long? Size { get; init; }
    public double? Duration { get; init; }
    public int? Bitrate { get; init; }
    public required string SourceId { get; init; }
    public required string SourceAppId { get; init; }
    public List<TrackPayload> Tracks { get; init; } = [];
}

sealed class TrackPayload
{
    public required Guid Id { get; init; }
    public required string Kind { get; init; }
    public required int Idx { get; init; }
    public string? Codec { get; init; }
    public string? CodecTag { get; init; }
    public string? Language { get; init; }
    public string? Title { get; init; }
    public string? Profile { get; init; }
    public int? BitRate { get; init; }
    public int? BitDepth { get; init; }
    public bool IsDefault { get; init; }
    public bool IsForced { get; init; }
    public bool IsHearingImpaired { get; init; }
    public bool IsExternal { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? Fps { get; init; }
    public double? AvgFps { get; init; }
    public string? AspectRatio { get; init; }
    public bool IsInterlaced { get; init; }
    public string? ColorRange { get; init; }
    public string? ColorSpace { get; init; }
    public string? ColorTransfer { get; init; }
    public string? ColorPrimaries { get; init; }
    public int? DvVersionMajor { get; init; }
    public int? DvVersionMinor { get; init; }
    public int? DvProfile { get; init; }
    public int? DvLevel { get; init; }
    public int? DvBlSignalCompatId { get; init; }
    public int? Channels { get; init; }
    public string? ChannelLayout { get; init; }
    public int? SampleRate { get; init; }
}
