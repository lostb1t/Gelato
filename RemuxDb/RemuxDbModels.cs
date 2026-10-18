namespace Gelato.RemuxDb;

/// <summary>
/// One file RemuxDB knows for a title, with the torrents and NZBs it was seen in. Field names are
/// RemuxDB's snake_case ones; missing values are left out of its responses.
/// </summary>
public sealed class RemuxDbVersion
{
    public string? ContentHash { get; set; }

    /// <summary>ffprobe's raw <c>format_name</c>, e.g. <c>matroska,webm</c>.</summary>
    public string? Container { get; set; }

    /// <summary>Seconds.</summary>
    public double? Duration { get; set; }

    public long? Size { get; set; }
    public long? Bitrate { get; set; }
    public bool VirtualChapters { get; set; }
    public List<RemuxDbChapter>? Chapters { get; set; }
    public List<RemuxDbSource>? Sources { get; set; }
    public List<RemuxDbTrack>? Tracks { get; set; }
}

public sealed class RemuxDbChapter
{
    public double StartTime { get; set; }
    public double? EndTime { get; set; }
    public string? Title { get; set; }
}

public sealed class RemuxDbSource
{
    /// <summary><c>torrent</c> or <c>nzb</c>.</summary>
    public string? Kind { get; set; }

    /// <summary>For torrents the path inside the torrent.</summary>
    public string? Filename { get; set; }

    public string? TorrentInfoHash { get; set; }
    public int? TorrentFileIdx { get; set; }
    public string? Indexer { get; set; }
    public string? IndexerGuid { get; set; }
}

/// <summary>
/// A track as RemuxDB returns it and takes it in a submission. Values are ffprobe's, except
/// <see cref="ColorRange"/> (<c>limited</c>/<c>full</c> instead of <c>tv</c>/<c>pc</c>).
/// </summary>
public sealed class RemuxDbTrack
{
    /// <summary><c>video</c>, <c>audio</c> or <c>subtitle</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>ffprobe stream index.</summary>
    public int Idx { get; set; }

    public string? Codec { get; set; }
    public string? Language { get; set; }
    public string? Title { get; set; }
    public long? BitRate { get; set; }
    public int? BitDepth { get; set; }
    public string? PixelFormat { get; set; }
    public string? Profile { get; set; }
    public double? Level { get; set; }
    public int? RefFrames { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public double? Fps { get; set; }
    public double? AvgFps { get; set; }
    public string? AspectRatio { get; set; }
    public int? Rotation { get; set; }
    public string? ColorPrimaries { get; set; }
    public string? ColorRange { get; set; }
    public string? ColorSpace { get; set; }
    public string? ColorTransfer { get; set; }
    public int? DvProfile { get; set; }
    public int? DvLevel { get; set; }
    public int? DvVersionMajor { get; set; }
    public int? DvVersionMinor { get; set; }
    public int? DvBlSignalCompatId { get; set; }
    public bool? DvRpuPresent { get; set; }
    public bool? DvBlPresent { get; set; }
    public bool? DvElPresent { get; set; }
    public bool? IsInterlaced { get; set; }
    public string? CodecTag { get; set; }
    public string? Comment { get; set; }
    public int? Channels { get; set; }
    public int? SampleRate { get; set; }
    public string? ChannelLayout { get; set; }
    public bool IsDefault { get; set; }
    public bool IsForced { get; set; }
    public bool IsHearingImpaired { get; set; }
    public bool IsExternal { get; set; }
    public bool? IsAnamorphic { get; set; }
    public bool? Hdr10PlusPresent { get; set; }
}

/// <summary>Body of <c>POST /api/mediainfo</c>.</summary>
public sealed class RemuxDbSubmission
{
    /// <summary><c>movie</c> or <c>episode</c>.</summary>
    public string Kind { get; set; } = "";

    public string Filename { get; set; } = "";
    public string? TorrentInfoHash { get; set; }
    public int? TorrentFileIdx { get; set; }
    public string Container { get; set; } = "";
    public long Size { get; set; }

    /// <summary>Seconds.</summary>
    public double Duration { get; set; }

    public long? Bitrate { get; set; }
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public RemuxDbExternalIds ExternalIds { get; set; } = new();
    public List<RemuxDbTrack> Tracks { get; set; } = [];
    public List<RemuxDbChapter>? Chapters { get; set; }
    public string? ClientId { get; set; }
}

public sealed class RemuxDbExternalIds
{
    public string? ImdbId { get; set; }
    public int? TmdbId { get; set; }
    public int? TvdbId { get; set; }
}

/// <summary>
/// What identifies a Stremio stream's file: the torrent it comes from, where the addon says so,
/// and the file's name and size.
/// </summary>
public sealed record StreamIdentity(string? InfoHash, int? FileIdx, long? Size, string? Filename)
{
    public bool IsEmpty =>
        string.IsNullOrEmpty(InfoHash) && Size is not > 0 && string.IsNullOrEmpty(Filename);
}

/// <summary>A title to look up: an IMDb id, plus season and episode for an episode.</summary>
public sealed record RemuxDbTitle(string ImdbId, int? Season, int? Episode)
{
    public bool IsEpisode => Season is not null && Episode is not null;

    public string CacheKey => IsEpisode ? $"{ImdbId}:{Season}:{Episode}" : ImdbId;

    /// <summary>
    /// The title of a Stremio id Gelato queries streams with: <c>tt123</c> for a movie,
    /// <c>tt123:1:2</c> for an episode. RemuxDB only takes IMDb ids.
    /// </summary>
    public static RemuxDbTitle? FromStremioId(string? id)
    {
        if (string.IsNullOrEmpty(id) || !id.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            return null;

        var parts = id.Split(':');
        if (parts.Length == 1)
            return new RemuxDbTitle(parts[0], null, null);

        return
            parts.Length == 3
            && int.TryParse(parts[1], out var season)
            && int.TryParse(parts[2], out var episode)
            ? new RemuxDbTitle(parts[0], season, episode)
            : null;
    }
}
