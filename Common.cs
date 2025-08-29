using System;
using System.Text;

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
    public static (string MediaType, string ExternalId)? Parse(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (!id.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase)) return null;

        var parts = id.Substring("stremio://".Length)
                      .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length != 2) return null;
        return (parts[0], parts[1]);
    }

    public static string Encode(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("id cannot be null or empty", nameof(input));
        //  _log.LogInformation("ExternalMedia: STEEMIOID {Guid}", input);
        // Remove the prefix
        var trimmed = input.Replace("stremio://", "");

        // Split into ["movie", "tmdb:1120768"]
        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            throw new ArgumentException("Invalid stremio id format", nameof(input));

        var mediaType = parts[0];
        var extId = parts[1];

        var shortType = mediaType[0].ToString();

        var encoded = $"{shortType}:{extId}";
        return encoded.Length > 16 ? encoded.Substring(0, 16) : encoded;
    }

    public static string Decode(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
            throw new ArgumentException("id cannot be null or empty", nameof(encoded));

        var parts = encoded.Split(':', 2); // split only once
        if (parts.Length < 2)
            throw new ArgumentException("Invalid encoded format", nameof(encoded));

        var shortType = parts[0];
        var extId = parts[1];

        // Map back to full type
        string mediaType = shortType switch
        {
            "m" => "movie",
            "s" => "series",
            "e" => "episode",
            _ => throw new ArgumentException($"Unknown media type code: {shortType}")
        };

        return $"stremio://{mediaType}/{extId}";
    }

}
