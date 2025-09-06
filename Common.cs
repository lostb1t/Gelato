using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

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

    // ==== Parsing ====

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
            "movie"  => StremioMediaType.Movie,
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

    // ==== Stable, non-reversible GUID (MD5 of canonical URI) ====

    public Guid ToGuid()
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(ToString()));
        return new Guid(hash);
    }

    // Convenience builders
    public StremioUri WithStream(string streamId) => new(MediaType, ExternalId, streamId);
    public StremioUri WithoutStream() => new(MediaType, ExternalId, null);

    public static StremioUri Movie(string externalId, string? streamId = null)
        => new(StremioMediaType.Movie, externalId, streamId);

    public static StremioUri Series(string externalId, string? streamId = null)
        => new(StremioMediaType.Series, externalId, streamId);
}