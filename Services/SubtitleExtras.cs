#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace Gelato.Services;

/// <summary>
/// The extra properties Stremio puts on a subtitles request (<c>filename</c>, <c>videoSize</c>,
/// <c>videoHash</c>), so an addon can return subtitles matched to the release that plays.
/// </summary>
public sealed record SubtitleExtras(string? Filename, long? VideoSize, string? VideoHash)
{
    /// <summary>The extras, null when none is known (the plain request is kept then).</summary>
    public static SubtitleExtras? Create(string? filename, long? videoSize, string? videoHash)
    {
        var e = new SubtitleExtras(
            string.IsNullOrWhiteSpace(filename) ? null : filename,
            videoSize is > 0 ? videoSize : null,
            string.IsNullOrWhiteSpace(videoHash) ? null : videoHash
        );
        return e.Filename is null && e.VideoSize is null && e.VideoHash is null ? null : e;
    }

    /// <summary><c>name=value</c> pairs with the values URL-encoded.</summary>
    public IEnumerable<string> ToPairs()
    {
        if (Filename is not null)
            yield return $"filename={Uri.EscapeDataString(Filename)}";
        if (VideoSize is { } size)
            yield return $"videoSize={size.ToString(CultureInfo.InvariantCulture)}";
        if (VideoHash is not null)
            yield return $"videoHash={Uri.EscapeDataString(VideoHash)}";
    }

    /// <summary>Part of the memo key: releases with other extras do not share an entry.</summary>
    public string CacheKey => string.Join('&', ToPairs());
}
