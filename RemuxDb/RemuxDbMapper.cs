using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace Gelato.RemuxDb;

/// <summary>
/// Matches Stremio streams to RemuxDB versions and converts between RemuxDB tracks and
/// Jellyfin's <see cref="MediaStream"/>s, the way Jellyfin's own ffprobe normaliser would.
/// </summary>
public static class RemuxDbMapper
{
    private static readonly string[] VideoExtensions =
    [
        ".mkv",
        ".mp4",
        ".m4v",
        ".avi",
        ".ts",
        ".m2ts",
        ".mov",
        ".webm",
        ".wmv",
        ".mpg",
        ".mpeg",
    ];

    /// <summary>Most streams RemuxDB may leave out between two it records.</summary>
    private const int MaxIndexGap = 32;

    /// <summary>Sizes within this share of each other count as the same file.</summary>
    private const double SizeTolerance = 0.01;

    /// <summary>
    /// The version the stream plays, or null when none matches for sure. By torrent first
    /// (narrowed by size, file name or index when the torrent holds several), then by exact size,
    /// then by file name.
    /// </summary>
    public static RemuxDbVersion? Match(IReadOnlyList<RemuxDbVersion> versions, StreamIdentity id)
    {
        if (versions.Count == 0 || id.IsEmpty)
            return null;

        var filename = BaseName(id.Filename);

        if (!string.IsNullOrEmpty(id.InfoHash))
        {
            var byTorrent = versions
                .Where(v =>
                    v.Sources?.Any(s =>
                        string.Equals(
                            s.TorrentInfoHash,
                            id.InfoHash,
                            StringComparison.OrdinalIgnoreCase
                        )
                    ) ?? false
                )
                .ToList();

            if (byTorrent.Count > 0)
            {
                // Packs list one version per file. The size is the surest pick, then the name;
                // the index last, as RemuxDB can number a torrent's files differently.
                var picked =
                    Single(byTorrent.Where(v => id.Size is > 0 && v.Size == id.Size))
                    ?? Single(
                        byTorrent.Where(v =>
                            filename is not null
                            && (v.Sources?.Any(s => BaseName(s.Filename) == filename) ?? false)
                        )
                    )
                    ?? Single(
                        byTorrent.Where(v =>
                            id.FileIdx is not null
                            && (
                                v.Sources?.Any(s =>
                                    string.Equals(
                                        s.TorrentInfoHash,
                                        id.InfoHash,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                    && s.TorrentFileIdx == id.FileIdx
                                ) ?? false
                            )
                        )
                    )
                    ?? (byTorrent.Count == 1 ? byTorrent[0] : null);

                if (picked is not null && SizeAgrees(picked, id))
                    return picked;
            }
        }

        if (id.Size is > 0)
        {
            var bySize = versions.Where(v => v.Size == id.Size).ToList();
            var picked =
                bySize.Count == 1
                    ? bySize[0]
                    : Single(
                        bySize.Where(v =>
                            filename is not null
                            && (v.Sources?.Any(s => BaseName(s.Filename) == filename) ?? false)
                        )
                    );
            if (picked is not null)
                return picked;
        }

        if (filename is not null)
        {
            var picked = Single(
                versions.Where(v =>
                    v.Sources?.Any(s => BaseName(s.Filename) == filename) ?? false
                )
            );
            if (picked is not null && SizeAgrees(picked, id))
                return picked;
        }

        return null;

        static RemuxDbVersion? Single(IEnumerable<RemuxDbVersion> candidates)
        {
            RemuxDbVersion? found = null;
            foreach (var candidate in candidates)
            {
                if (found is not null && !ReferenceEquals(found, candidate))
                    return null;
                found = candidate;
            }

            return found;
        }
    }

    private static bool SizeAgrees(RemuxDbVersion version, StreamIdentity id) =>
        id.Size is not > 0
        || version.Size is not > 0
        || Math.Abs(version.Size.Value - id.Size.Value) <= version.Size.Value * SizeTolerance;

    /// <summary>A file name without directories and video extension, lower case.</summary>
    internal static string? BaseName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var file = name.Replace('\\', '/');
        file = file[(file.LastIndexOf('/') + 1)..].Trim();
        foreach (var ext in VideoExtensions)
        {
            if (file.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                file = file[..^ext.Length];
                break;
            }
        }

        return file.Length == 0 ? null : file.ToLowerInvariant();
    }

    /// <summary>
    /// Whether a version has what playback needs to skip its own probe: a video track with a
    /// codec and dimensions, and a runtime.
    /// </summary>
    public static bool IsUsable(RemuxDbVersion version) =>
        version.Duration is > 0
        && (
            version.Tracks?.Any(t =>
                t.Kind == "video" && !string.IsNullOrEmpty(t.Codec) && t.Width > 0 && t.Height > 0
            ) ?? false
        );

    /// <summary>
    /// A version's tracks as Jellyfin stores a probed file's. Indexes are ffmpeg's: Jellyfin maps a
    /// track to ffmpeg by its position in the list, so gaps RemuxDB leaves (data streams it does
    /// not record) are filled with data streams.
    /// </summary>
    public static List<MediaStream> ToMediaStreams(
        RemuxDbVersion version,
        EmbeddedSubtitleOptions embeddedSubtitles
    )
    {
        var streams = new List<MediaStream>();
        var tracks = (version.Tracks ?? [])
            .Where(t => !t.IsExternal && t.Kind is "video" or "audio" or "subtitle")
            .OrderBy(t => t.Idx)
            .ToList();

        var nextIdx = 0;
        foreach (var track in tracks)
        {
            // Out of order or repeated indexes, or a gap no file has: the list cannot be lined
            // up with the file.
            if (track.Idx < nextIdx || track.Idx - nextIdx > MaxIndexGap)
                return [];

            for (; nextIdx < track.Idx; nextIdx++)
                streams.Add(new MediaStream { Type = MediaStreamType.Data, Index = streams.Count });

            streams.Add(ToMediaStream(track, streams.Count));
            nextIdx = track.Idx + 1;
        }

        // What Jellyfin's probe drops for the library's embedded subtitle setting.
        if (embeddedSubtitles is EmbeddedSubtitleOptions.AllowText or EmbeddedSubtitleOptions.AllowNone)
            RemoveSubtitles(streams, text: false);
        if (embeddedSubtitles is EmbeddedSubtitleOptions.AllowImage or EmbeddedSubtitleOptions.AllowNone)
            RemoveSubtitles(streams, text: true);

        return streams;

        // Removed subtitles keep their place as data streams, so the rest still line up.
        static void RemoveSubtitles(List<MediaStream> streams, bool text)
        {
            for (var i = 0; i < streams.Count; i++)
            {
                if (
                    streams[i].Type == MediaStreamType.Subtitle
                    && streams[i].IsTextSubtitleStream == text
                )
                {
                    streams[i] = new MediaStream { Type = MediaStreamType.Data, Index = i };
                }
            }
        }
    }

    private static MediaStream ToMediaStream(RemuxDbTrack t, int index)
    {
        var stream = new MediaStream
        {
            Index = index,
            Codec = t.Codec,
            Language = t.Language,
            Title = string.Equals(t.Title, "cc", StringComparison.OrdinalIgnoreCase)
                ? null
                : t.Title,
            Profile = t.Profile,
            IsDefault = t.IsDefault,
            IsForced = t.IsForced,
            IsHearingImpaired = t.IsHearingImpaired,
            BitRate = t.BitRate is > 0 and <= int.MaxValue ? (int)t.BitRate.Value : null,
            BitDepth = t.BitDepth,
            Comment = t.Comment,
            CodecTag = t.CodecTag,
        };

        switch (t.Kind)
        {
            case "video":
                stream.Type = MediaStreamType.Video;
                stream.Width = t.Width;
                stream.Height = t.Height;
                stream.Level = t.Level;
                stream.PixelFormat = t.PixelFormat;
                stream.RefFrames = t.RefFrames is > 0 ? t.RefFrames : null;
                stream.RealFrameRate = (float?)t.Fps;
                stream.AverageFrameRate = (float?)(t.AvgFps ?? t.Fps);
                stream.AspectRatio = t.AspectRatio;
                stream.IsAnamorphic = t.IsAnamorphic;
                stream.IsInterlaced = t.IsInterlaced ?? false;
                stream.Rotation = t.Rotation;
                stream.ColorPrimaries = t.ColorPrimaries;
                stream.ColorSpace = t.ColorSpace;
                stream.ColorTransfer = t.ColorTransfer;
                stream.ColorRange = t.ColorRange switch
                {
                    "limited" => "tv",
                    "full" => "pc",
                    _ => t.ColorRange,
                };
                stream.DvProfile = t.DvProfile;
                stream.DvLevel = t.DvLevel;
                stream.DvVersionMajor = t.DvVersionMajor;
                stream.DvVersionMinor = t.DvVersionMinor;
                stream.DvBlSignalCompatibilityId = t.DvBlSignalCompatId;
                stream.RpuPresentFlag = Flag(t.DvRpuPresent);
                stream.BlPresentFlag = Flag(t.DvBlPresent);
                stream.ElPresentFlag = Flag(t.DvElPresent);
                stream.Hdr10PlusPresentFlag = t.Hdr10PlusPresent == true ? true : null;
                stream.BitDepth ??= t.PixelFormat switch
                {
                    "yuv420p" or "yuv444p" => 8,
                    "yuv420p10le" or "yuv444p10le" => 10,
                    "yuv420p12le" or "yuv444p12le" => 12,
                    _ => null,
                };
                break;
            case "audio":
                stream.Type = MediaStreamType.Audio;
                stream.Channels = t.Channels;
                stream.SampleRate = t.SampleRate;
                stream.ChannelLayout = t.ChannelLayout;
                break;
            default:
                stream.Type = MediaStreamType.Subtitle;
                stream.Codec = t.Codec?.ToLowerInvariant() switch
                {
                    "dvb_subtitle" => "DVBSUB",
                    "dvb_teletext" => "DVBTXT",
                    "dvd_subtitle" => "DVDSUB",
                    "hdmv_pgs_subtitle" => "PGSSUB",
                    _ => t.Codec,
                };
                break;
        }

        return stream;

        static int? Flag(bool? value) =>
            value switch
            {
                true => 1,
                false => 0,
                null => null,
            };
    }

    /// <summary>
    /// Jellyfin's name for an ffprobe <c>format_name</c> (<c>matroska,webm</c> is <c>mkv</c>),
    /// as its probe normalises it.
    /// </summary>
    public static string? NormalizeContainer(string? format, IReadOnlyList<MediaStream> streams)
    {
        if (string.IsNullOrWhiteSpace(format))
            return null;

        string[] webmVideo = ["av1", "vp8", "vp9"];
        string[] webmAudio = ["opus", "vorbis"];
        var webmCompatible = streams.All(s =>
            s.Type switch
            {
                MediaStreamType.Video => webmVideo.Contains(s.Codec, StringComparer.OrdinalIgnoreCase),
                MediaStreamType.Audio => webmAudio.Contains(s.Codec, StringComparer.OrdinalIgnoreCase),
                _ => false,
            }
        );

        var parts = format
            .Split(',')
            .Select(p =>
                p.Trim().ToLowerInvariant() switch
                {
                    "mpegvideo" => "mpeg",
                    "mpegts" => "ts",
                    "matroska" => "mkv",
                    "webm" => webmCompatible ? "webm" : "",
                    var other => other,
                }
            )
            .Where(p => p.Length > 0);

        return string.Join(',', parts);
    }

    /// <summary>
    /// A probed file's streams as RemuxDB tracks, or null when a track lacks what RemuxDB requires
    /// (codec and dimensions and frame rate of a video track; codec, channels and sample rate of
    /// an audio track). External streams are left out; the rest get their ffmpeg index back.
    /// </summary>
    public static List<RemuxDbTrack>? ToTracks(IReadOnlyList<MediaStream> streams)
    {
        var tracks = new List<RemuxDbTrack>();
        var idx = 0;
        foreach (var s in streams.Where(s => !s.IsExternal).OrderBy(s => s.Index))
        {
            var track = new RemuxDbTrack
            {
                Idx = idx++,
                Codec = s.Codec,
                Language = s.Language,
                Title = s.Title,
                Comment = s.Comment,
                Profile = s.Profile,
                BitRate = s.BitRate,
                BitDepth = s.BitDepth,
                CodecTag = s.CodecTag,
                IsDefault = s.IsDefault,
                IsForced = s.IsForced,
                IsHearingImpaired = s.IsHearingImpaired,
            };

            switch (s.Type)
            {
                case MediaStreamType.Video:
                    {
                        track.Kind = "video";
                        track.Width = s.Width;
                        track.Height = s.Height;
                        track.Fps = s.RealFrameRate ?? s.AverageFrameRate;
                        track.AvgFps = s.AverageFrameRate;
                        track.Level = s.Level;
                        track.RefFrames = s.RefFrames;
                        track.PixelFormat = s.PixelFormat;
                        track.AspectRatio = s.AspectRatio;
                        track.Rotation = s.Rotation;
                        track.IsAnamorphic = s.IsAnamorphic;
                        track.IsInterlaced = s.IsInterlaced;
                        track.ColorPrimaries = s.ColorPrimaries;
                        track.ColorSpace = s.ColorSpace;
                        track.ColorTransfer = s.ColorTransfer;
                        track.ColorRange = s.ColorRange switch
                        {
                            "tv" => "limited",
                            "pc" => "full",
                            _ => s.ColorRange,
                        };
                        track.DvProfile = s.DvProfile;
                        track.DvLevel = s.DvLevel;
                        track.DvVersionMajor = s.DvVersionMajor;
                        track.DvVersionMinor = s.DvVersionMinor;
                        track.DvBlSignalCompatId = s.DvBlSignalCompatibilityId;
                        track.DvRpuPresent = s.RpuPresentFlag is { } rpu ? rpu == 1 : null;
                        track.DvBlPresent = s.BlPresentFlag is { } bl ? bl == 1 : null;
                        track.DvElPresent = s.ElPresentFlag is { } el ? el == 1 : null;
                        track.Hdr10PlusPresent = s.Hdr10PlusPresentFlag;
                        if (
                            string.IsNullOrEmpty(track.Codec)
                            || track.Width is not > 0
                            || track.Height is not > 0
                            || track.Fps is not > 0
                        )
                        {
                            return null;
                        }
                        break;
                    }
                case MediaStreamType.Audio:
                    {
                        track.Kind = "audio";
                        track.Channels = s.Channels;
                        track.SampleRate = s.SampleRate;
                        track.ChannelLayout = s.ChannelLayout;
                        if (
                            string.IsNullOrEmpty(track.Codec)
                            || track.Channels is not > 0
                            || track.SampleRate is not > 0
                        )
                        {
                            return null;
                        }
                        break;
                    }
                case MediaStreamType.Subtitle:
                    {
                        track.Kind = "subtitle";
                        track.Codec = s.Codec?.ToUpperInvariant() switch
                        {
                            "DVBSUB" => "dvb_subtitle",
                            "DVBTXT" => "dvb_teletext",
                            "DVDSUB" => "dvd_subtitle",
                            "PGSSUB" => "hdmv_pgs_subtitle",
                            _ => s.Codec,
                        };
                        break;
                    }
                default:
                    // Data streams and embedded images are not tracks to RemuxDB, but they
                    // count in ffmpeg's numbering.
                    continue;
            }

            tracks.Add(track);
        }

        return tracks.Any(t => t.Kind == "video") ? tracks : null;
    }

    /// <summary>
    /// Whether a file's runtime fits the title's: between half and double of it. False when the
    /// title's runtime is unknown.
    /// </summary>
    public static bool RuntimeFits(long runtime, long? expected) =>
        expected is > 0 && runtime >= expected.Value / 2 && runtime <= expected.Value * 2;

    /// <summary>Seconds as Jellyfin's ticks.</summary>
    public static long ToTicks(double seconds) =>
        (long)Math.Round(seconds * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero);
}
