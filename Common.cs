using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gelato.Common;

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

    private static readonly Regex Rx =
        new(@"^stremio://(?<type>movie|series)/(?<ext>[^/\s]+)(?:/(?<stream>[^/\s]+))?$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static StremioUri FromString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Value cannot be null or empty.", nameof(value));

        if (TryParse(value, out var sid) && sid is not null)
            return sid;

        throw new FormatException($"Invalid StremioId string: {value}");
    }

    public static StremioUri? FromMeta(StremioMeta meta)
    {
        if (TryParse(meta.Id, out var sid) && sid is not null)
            return sid;

        return null;
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

    // without stream id
    public string ToBaseString()
    {
        var type = MediaType == StremioMediaType.Movie ? "movie" : "series";
        return $"stremio://{type}/{ExternalId}";
    }

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

public static class Utils
{
    public static long? ParseToTicks(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        input = input.Trim().ToLowerInvariant();

        // Try built-in parse (hh:mm:ss)
        if (TimeSpan.TryParse(input, out var ts))
            return ts.Ticks;

        // Try XML ISO8601 style (PT2H29M)
        try
        {
            ts = System.Xml.XmlConvert.ToTimeSpan(input);
            return ts.Ticks;
        }
        catch
        {
            // ignore
        }
        // Regex fallback for human formats like "2h29min"
        var h = Regex.Match(input, @"(\d+)\s*h");
        var m = Regex.Match(input, @"(\d+)\s*min");
        var s = Regex.Match(input, @"(\d+)\s*s(ec)?");

        int hours = h.Success ? int.Parse(h.Groups[1].Value) : 0;
        int mins = m.Success ? int.Parse(m.Groups[1].Value) : 0;
        int secs = s.Success ? int.Parse(s.Groups[1].Value) : 0;

        // If plain number like "149" → minutes
        if (!h.Success && !m.Success && !s.Success && int.TryParse(input, out var onlyNum))
            mins = onlyNum;

        return new TimeSpan(hours, mins, secs).Ticks;
    }
}



public sealed class FileCache
{
    private readonly string _basePath;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class Envelope<T>
    {
        public T? Value { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
    }

    public FileCache(string basePath)
    {
        _basePath = basePath ?? throw new ArgumentNullException(nameof(basePath));
        Directory.CreateDirectory(_basePath);
    }

    public async Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        var path = PathFor(key);
        await using var fs = File.Create(path);
        var envelope = new Envelope<T>
        {
            Value = value,
            // ExpiresAt = ttl.HasValue ? DateTimeOffset.UtcNow.Add(ttl.Value) : null
        };
        await JsonSerializer.SerializeAsync(fs, envelope, JsonOpts, ct).ConfigureAwait(false);
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            return default;

        var path = PathFor(key);
        if (!File.Exists(path))
            return default;

        try
        {
            await using var fs = File.OpenRead(path);
            var envelope = await JsonSerializer
                .DeserializeAsync<Envelope<T>>(fs, JsonOpts, ct)
                .ConfigureAwait(false);

            if (envelope is null)
                return default;

            if (envelope.ExpiresAt.HasValue && envelope.ExpiresAt.Value <= DateTimeOffset.UtcNow)
            {
                try { File.Delete(path); } catch { /* ignore */ }
                return default;
            }

            return envelope.Value;
        }
        catch
        {
            try { File.Delete(path); } catch { /* ignore */ }
            return default;
        }
    }

    public void Remove(string key)
    {
        var path = PathFor(key);
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private string PathFor(string key)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return Path.Combine(_basePath, sb.ToString() + ".json");
    }
}