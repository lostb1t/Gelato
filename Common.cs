using System;
using System.Text;
using Jellyfin.Plugin.ExternalMedia.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Plugin.ExternalMedia.Common;

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
        return str.TrimEnd('\0'); // remove padding
    }
}

public static class StremioId
{
    /// <summary>
    /// Decode a Guid (created with GuidCodec) back into a StremioId and parse it.
    /// </summary>
    public static bool TryParseFromGuid(Guid guid, out string stremioId, out StremioMediaType mediaType, out string externalId)
    {
        stremioId = default!;
        mediaType = default;
        externalId = default!;

        try
        {
            var decoded = GuidCodec.DecodeString(guid);
            stremioId = FromCompactId(decoded);

            if (TryParse(stremioId, out var result))
            {
                mediaType = result.MediaType;
                externalId = result.ExternalId;
                return true;
            }
        }
        catch
        {
            // swallow, return false
        }
        return false;
    }

    /// <summary>
    /// Encode a StremioId into a Guid using GuidCodec.
    /// </summary>
    public static Guid ToGuid(string stremioId)
    {
        var compact = ToCompactId(stremioId);
        return GuidCodec.EncodeString(compact);
    }

    /// <summary>
    /// Try to get (mediaType, externalId) directly from a Guid.
    /// </summary>
    public static bool TryGetParts(Guid guid, out (StremioMediaType MediaType, string ExternalId) result)
    {
        result = default;
        if (TryParseFromGuid(guid, out _, out var mt, out var ext))
        {
            result = (mt, ext);
            return true;
        }
        return false;
    }

    public static (StremioMediaType MediaType, string ExternalId) Parse(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Value cannot be null or empty.", nameof(id));

        if (!id.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Invalid StremioId: {id}");

        var parts = id.Substring("stremio://".Length)
                      .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2)
            throw new FormatException($"Invalid StremioId: {id}");

        if (!Enum.TryParse(parts[0], true, out StremioMediaType mediaType))
            throw new FormatException($"Unknown media type in StremioId: {parts[0]}");

        return (mediaType, parts[1]);
    }

    public static bool TryParse(string id, out (StremioMediaType MediaType, string ExternalId) result)
    {
        try
        {
            result = Parse(id);
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }

    public static string ToCompactId(string stremioId)
    {
        if (string.IsNullOrWhiteSpace(stremioId))
            throw new ArgumentException("id cannot be null or empty", nameof(stremioId));

        var trimmed = stremioId.Replace("stremio://", "");
        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new ArgumentException("Invalid stremio id format", nameof(stremioId));

        var mediaType = parts[0];
        var extId = parts[1];
        var shortType = mediaType[0].ToString();

        var compact = $"{shortType}:{extId}";
        return compact.Length > 16 ? compact.Substring(0, 16) : compact;
    }

    public static string FromCompactId(string compactId)
    {
        if (string.IsNullOrWhiteSpace(compactId))
            throw new ArgumentException("id cannot be null or empty", nameof(compactId));

        var parts = compactId.Split(':', 2);
        if (parts.Length < 2)
            throw new ArgumentException("Invalid compact id format", nameof(compactId));

        var shortType = parts[0];
        var extId = parts[1];

        var mediaType = shortType switch
        {
            "m" => "movie",
            "s" => "series",
            _ => throw new ArgumentException($"Unknown media type code: {shortType}")
        };

        return $"stremio://{mediaType}/{extId}";
    }

}

public static class Helpers
{

}
