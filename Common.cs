using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.ExternalMedia.Common;

public sealed class StremioUri
{
    public StremioMediaType MediaType { get; }
    public string ExternalId { get; }
    public string? StreamId { get; }

    public StremioUri(StremioMediaType mediaType, string externalId, string? streamId = null)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            throw new ArgumentException("externalId cannot be null or empty.", nameof(externalId));

        MediaType = mediaType;
        ExternalId = externalId;
        StreamId = string.IsNullOrWhiteSpace(streamId) ? null : streamId;
    }

    #region URI parsing/format

    private static readonly Regex Rx =
        new(@"^stremio://(?<type>movie|series)/(?<ext>[^/\s]+)(?:/(?<stream>[^/\s]+))?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static StremioUri LoadFromString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be null or empty.", nameof(value));

        if (TryParse(value, out var sid) && sid is not null)
            return sid;

        throw new FormatException($"Invalid StremioId string: {value}");
    }

    public static StremioUri Parse(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Value cannot be null or empty.", nameof(id));

        var m = Rx.Match(id);
        if (!m.Success)
            throw new FormatException($"Invalid StremioId: {id}");

        var typeStr = m.Groups["type"].Value.ToLowerInvariant();
        var ext = m.Groups["ext"].Value;
        var stream = m.Groups["stream"].Success ? m.Groups["stream"].Value : null;

        var mediaType = typeStr switch
        {
            "movie" => StremioMediaType.Movie,
            "series" => StremioMediaType.Series,
            _ => throw new FormatException($"Unknown media type in StremioId: {typeStr}")
        };

        return new StremioUri(mediaType, ext, stream);
    }

    public static bool TryParse(string id, out StremioUri? value)
    {
        try { value = Parse(id); return true; }
        catch { value = null; return false; }
    }

    public override string ToString()
    {
        var type = MediaType == StremioMediaType.Movie ? "movie" : "series";
        return StreamId is null
            ? $"stremio://{type}/{ExternalId}"
            : $"stremio://{type}/{ExternalId}/{StreamId}";
    }

    #endregion

    #region Compact string

    private const char Sep = '|';

    // m|extId or m|extId|streamId
    public string ToCompactString()
    {
        var shortType = MediaType == StremioMediaType.Movie ? "m" : "s";
        return StreamId is null
            ? $"{shortType}{Sep}{ExternalId}"
            : $"{shortType}{Sep}{ExternalId}{Sep}{StreamId}";
    }

    public static StremioUri FromCompactString(string compact)
    {
        if (string.IsNullOrWhiteSpace(compact))
            throw new ArgumentException("compact cannot be null or empty", nameof(compact));

        var parts = compact.Split(Sep);
        if (parts.Length < 2)
            throw new FormatException($"Invalid compact id: {compact}");

        var mediaType = parts[0] switch
        {
            "m" => StremioMediaType.Movie,
            "s" => StremioMediaType.Series,
            _   => throw new FormatException($"Unknown media short code: {parts[0]}")
        };

        var ext = parts[1];
        var stream = parts.Length >= 3 ? parts[2] : null;

        return new StremioUri(mediaType, ext, stream);
    }

    #endregion

    #region GUID helpers

    /// Deterministic, non-reversible GUID derived from MD5 of the canonical URI.
    public Guid ToGuid()
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(ToString()));
        return new Guid(hash);
    }

    /// Reversible (best-effort) GUID by UTF-8 pad/truncate of the compact string.
    public Guid ToGuidEncoded() => GuidCodec.EncodeString(ToCompactString());

    public static StremioUri FromGuidEncoded(Guid guid)
    {
        var decoded = GuidCodec.DecodeString(guid);
        return FromCompactString(decoded);
    }

    public static bool TryParseFromGuidEncoded(Guid guid, out StremioUri? value)
    {
        value = null;
        try
        {
            value = FromGuidEncoded(guid);
            return true;
        }
        catch { return false; }
    }

    public static bool TryGetPartsFromGuidEncoded(Guid guid, out StremioMediaType mediaType, out string externalId, out string? streamId)
    {
        mediaType = default;
        externalId = default!;
        streamId = null;

        if (TryParseFromGuidEncoded(guid, out var sid) && sid is not null)
        {
            mediaType = sid.MediaType;
            externalId = sid.ExternalId;
            streamId = sid.StreamId;
            return true;
        }
        return false;
    }

    #endregion

    #region Builders

    public StremioUri WithStream(string streamId) => new(MediaType, ExternalId, streamId);
    public StremioUri WithoutStream() => new(MediaType, ExternalId, null);

    public static StremioUri Movie(string externalId, string? streamId = null)
        => new(StremioMediaType.Movie, externalId, streamId);

    public static StremioUri Series(string externalId, string? streamId = null)
        => new(StremioMediaType.Series, externalId, streamId);

    #endregion
}

public static class GuidCodec
{
    public static Guid EncodeString(string key)
    {
        var bytes = Encoding.UTF8.GetBytes(key);
        Array.Resize(ref bytes, 16);
        return new Guid(bytes);
    }

    public static string DecodeString(Guid guid)
    {
        var bytes = guid.ToByteArray();
        var str = Encoding.UTF8.GetString(bytes);
        return str.TrimEnd('\0');
    }
}