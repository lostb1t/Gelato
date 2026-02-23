using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Gelato;

public class GelatoStremioProvider(
    string baseUrl,
    IHttpClientFactory http,
    ILogger<GelatoStremioProvider> log
)
{
    private StremioManifest? _manifest;
    private StremioCatalog? _movieSearchCatalog;
    private StremioCatalog? _seriesSearchCatalog;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private HttpClient NewClient()
    {
        var c = http.CreateClient(nameof(GelatoStremioProvider));
        c.Timeout = TimeSpan.FromSeconds(30);
        return c;
    }

    private string BuildUrl(string[] segments, IEnumerable<string>? extras = null)
    {
        var parts = segments.Select(Uri.EscapeDataString).ToArray();
        var path = string.Join("/", parts);

        var extrasPart = string.Empty;
        if (extras != null)
        {
            var enumerable = extras.ToList();
            extrasPart = enumerable.Count != 0 ? "/" + string.Join("&", enumerable) : string.Empty;
        }

        var url = $"{baseUrl}/{path}{extrasPart}.json";
        url = url.Replace("%3A", ":").Replace("%3a", ":");
        return url;
    }

    private async Task<T?> GetJsonAsync<T>(string url)
    {
        log.LogDebug("GetJsonAsync: requesting {Url}", url);

        try
        {
            var c = NewClient();
            var resp = await c.GetAsync(url).ConfigureAwait(false); // No using statement

            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning(
                    "GetJsonAsync: request failed for {Url} with {StatusCode} {ReasonPhrase}",
                    url,
                    resp.StatusCode,
                    resp.ReasonPhrase
                );

                throw new HttpRequestException(
                    $"HTTP {resp.StatusCode}: {resp.ReasonPhrase}",
                    null,
                    resp.StatusCode
                );
            }

            await using var s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(s, JsonOpts).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "GetJsonAsync: error fetching or parsing {Url}", url);
            throw;
        }
    }

    public async Task<StremioManifest?> GetManifestAsync(bool force = false)
    {
        if (!force && _manifest is not null)
            return _manifest;
        try
        {
            var url = $"{baseUrl}/manifest.json";
            var m = await GetJsonAsync<StremioManifest>(url);
            _manifest = m;

            if (m?.Catalogs != null)
            {
                _movieSearchCatalog = m
                    .Catalogs.Where(c =>
                        string.Equals(
                            c.Type,
                            nameof(StremioMediaType.Movie),
                            StringComparison.CurrentCultureIgnoreCase
                        )
                        && c.Extra.Any(e =>
                            string.Equals(e.Name, "search", StringComparison.OrdinalIgnoreCase)
                        )
                    )
                    .OrderBy(c => c.Id.Contains("people", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();

                _seriesSearchCatalog = m
                    .Catalogs.Where(c =>
                        string.Equals(
                            c.Type,
                            nameof(StremioMediaType.Series),
                            StringComparison.CurrentCultureIgnoreCase
                        )
                        && c.Extra.Any(e =>
                            string.Equals(e.Name, "search", StringComparison.OrdinalIgnoreCase)
                        )
                    )
                    .OrderBy(c => c.Id.Contains("people", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
            }

            if (_movieSearchCatalog == null)
            {
                log.LogWarning("manifest has no search-capable movie catalog");
            }
            else
            {
                log.LogInformation(
                    "manifest uses movie search catalog: {Id}",
                    _movieSearchCatalog.Id
                );
            }

            if (_seriesSearchCatalog == null)
            {
                log.LogWarning("manifest has no search-capable series catalog");
            }
            else
            {
                log.LogInformation(
                    "manifest uses series search catalog: {Id}",
                    _seriesSearchCatalog.Id
                );
            }
            return m;
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "GetManifestAsync: cannot fetch manifest");
            return null;
        }
    }

    public async Task<bool> IsReady()
    {
        var m = await GetManifestAsync();
        return m is not null;
    }

    public async Task<StremioMeta?> GetMetaAsync(string id, StremioMediaType mediaType)
    {
        var url = BuildUrl(["meta", mediaType.ToString().ToLower(), id]);
        var r = await GetJsonAsync<StremioMetaResponse>(url);
        return r?.Meta;
    }

    public async Task<StremioMeta?> GetMetaAsync(BaseItem item)
    {
        var id = item.GetProviderId("Imdb");
        if (id is null)
        {
            log.LogWarning("GetMetaAsync: {Name} has no imdb ID", item.Name);
            id = item.GetProviderId("Tmdb");
            if (id is null)
            {
                log.LogWarning("GetMetaAsync: {Name} has no imdb and tmdb ID", item.Name);
                return null;
            }
            id = $"tmdb:{id}";
        }
        var url = BuildUrl(["meta", item.GetBaseItemKind().ToStremio().ToString().ToLower(), id]);
        var r = await GetJsonAsync<StremioMetaResponse>(url);
        return r?.Meta;
    }

    public async Task<List<StremioStream>> GetStreamsAsync(StremioUri uri)
    {
        return await GetStreamsAsync(uri.ExternalId, uri.MediaType);
    }

    private async Task<List<StremioStream>> GetStreamsAsync(string id, StremioMediaType mediaType)
    {
        var url = BuildUrl(["stream", mediaType.ToString().ToLower(), id]);
        var r = await GetJsonAsync<StremioStreamsResponse>(url);

        var error = r?.GetError();
        if (error is not null)
        {
            throw new InvalidOperationException($"Stremio returned an error: {error}");
        }

        return r?.Streams ?? [];
    }

    public async Task<List<StremioSubtitle>> GetSubtitlesAsync(
        string id,
        StremioMediaType mediaType
    )
    {
        var url = BuildUrl(["subtitles", mediaType.ToString().ToLower(), id]);
        var r = await GetJsonAsync<StremioSubtitleResponse>(url);
        return r.Subtitles;
    }

    public async Task<IReadOnlyList<StremioMeta>> GetCatalogMetasAsync(
        string id,
        string mediaType,
        string? search = null,
        int? skip = null
    )
    {
        var extras = new List<string>();
        if (!string.IsNullOrWhiteSpace(search))
            extras.Add($"search={Uri.EscapeDataString(search)}");
        if (skip is > 0)
            extras.Add($"skip={skip}");

        // seen maybe one type thats capital, but thats their issue
        var url = BuildUrl(["catalog", mediaType.ToLower(), id], extras);
        var r = await GetJsonAsync<StremioCatalogResponse>(url);
        return r?.Metas ?? [];
    }

    public async Task<IReadOnlyList<StremioMeta>> SearchAsync(
        string query,
        StremioMediaType mediaType,
        int? skip = null
    )
    {
        var manifest = await GetManifestAsync();
        if (manifest == null)
            return [];

        var catalog = mediaType switch
        {
            StremioMediaType.Movie => _movieSearchCatalog,
            StremioMediaType.Series => _seriesSearchCatalog,
            _ => null,
        };

        if (catalog == null)
        {
            log.LogError(
                "SearchAsync: {mediaType} has no search catalog, please enable one in aiostreams.",
                mediaType
            );
            return [];
        }

        return await GetCatalogMetasAsync(catalog.Id, mediaType.ToString(), query, skip);
    }
}

#region Request Models
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable CollectionNeverUpdated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

public class StremioManifest
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Description { get; set; }
    public List<StremioCatalog> Catalogs { get; set; } = new();
    public List<StremioResource> Resources { get; set; } = new();
    public List<string> Types { get; set; } = new();
    public string? Background { get; set; }
    public string? Logo { get; set; }
    public StremioBehaviorHints? BehaviorHints { get; set; }
    public List<StremioCatalog> AddonCatalogs { get; set; } = new();
}

public class StremioCatalog
{
    // we dont cast to enum cause types is not a static set
    public string Type { get; set; } = "";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<StremioExtra> Extra { get; set; } = new();

    public bool IsSearchCapable()
    {
        return Extra.Any(e => string.Equals(e.Name, "search", StringComparison.OrdinalIgnoreCase));
    }
}

public class StremioExtra
{
    public string Name { get; set; } = "";
    public bool IsRequired { get; set; }
    public List<string> Options { get; set; } = new();
}

public class StremioResource
{
    public string Name { get; set; } = "";
    public List<string> Types { get; set; } = new();
    public List<string> IdPrefixes { get; set; } = new();
}

public class StremioCatalogResponse
{
    public List<StremioMeta>? Metas { get; set; }
}

public struct StremioSubtitle
{
    public string Id { get; set; }
    public string Url { get; set; }
    public string? Lang { get; set; }
    public int? SubId { get; set; }
    public bool? AiTranslated { get; set; }
    public bool? FromTrusted { get; set; }
    public int? UploaderId { get; set; }

    [JsonPropertyName("lang_code")]
    public string? LangCode { get; set; }
    public string? Title { get; set; }
    public string? Moviehash { get; set; }
}

public struct StremioSubtitleResponse
{
    public List<StremioSubtitle> Subtitles { get; set; }
}

public class StremioMetaResponse
{
    public StremioMeta Meta { get; set; } = null!;
}

public class StremioMeta
{
    public required string Id { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public StremioMediaType Type { get; set; } = StremioMediaType.Unknown;
    public string? Name { get; set; }
    public string? Title { get; set; }
    public string? Poster { get; set; }
    public List<string>? Genres { get; set; }
    public string? ImdbRating { get; set; }
    public string? ReleaseInfo { get; set; }
    public string? Description { get; set; }
    public string? Overview { get; set; }
    public List<StremioTrailer>? Trailers { get; set; }
    public List<StremioLink>? Links { get; set; }
    public string? Background { get; set; }
    public string? Logo { get; set; }
    public List<StremioMeta>? Videos { get; set; }
    public string? Runtime { get; set; }
    public string? Country { get; set; }
    public StremioBehaviorHints? BehaviorHints { get; set; }
    public List<string>? Genre { get; set; }

    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }
    public DateTime? Released { get; set; }

    [JsonConverter(typeof(SafeStringEnumConverter<StremioStatus>))]
    // ReSharper disable once MemberCanBePrivate.Global
    public StremioStatus? Status { get; set; } = StremioStatus.Unknown;

    [JsonConverter(typeof(NullableIntLenientConverter))]
    public int? Year { get; set; }
    public string? Slug { get; set; }
    public List<StremioTrailerStream>? TrailerStreams { get; set; }

    // ReSharper disable once InconsistentNaming
    public StremioAppExtras? App_Extras { get; set; }
    public string? Thumbnail { get; set; }
    public int? Episode { get; set; }
    public int? Season { get; set; }
    public int? Number { get; set; }
    public DateTime? FirstAired { get; set; }
    public Guid? Guid { get; set; }

    public string? TvdbEpisodeId()
    {
        if (!Uri.TryCreate(Thumbnail, UriKind.Absolute, out var uri))
            return null;

        if (!uri.Host.Contains("thetvdb.com", StringComparison.OrdinalIgnoreCase))
            return null;

        var lastSegment = uri.Segments.Last().TrimEnd('/');

        return int.TryParse(lastSegment, out _) ? lastSegment : null;
    }

    public string GetName()
    {
        if (!string.IsNullOrWhiteSpace(Title))
        {
            return Title;
        }
        if (!string.IsNullOrWhiteSpace(Name))
        {
            return Name;
        }
        return "";
    }

    public Dictionary<string, string> GetProviderIds()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(Id))
        {
            if (Id.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase))
            {
                dict[nameof(MetadataProvider.Tmdb)] = Id["tmdb:".Length..];
            }
            else if (Id.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                dict[nameof(MetadataProvider.Imdb)] = Id;
            }
        }

        if (!string.IsNullOrWhiteSpace(ImdbId))
        {
            dict[nameof(MetadataProvider.Imdb)] = ImdbId;
        }

        return dict;
    }

    public int? GetYear()
    {
        if (Year is not null)
            return Year;

        if (Released is { } dt)
            return dt.Year;

        // "2007-2019", "2020-", or "2015"
        if (!string.IsNullOrWhiteSpace(ReleaseInfo))
        {
            var s = ReleaseInfo.Trim();

            if (
                s.Length >= 4
                && int.TryParse(s.AsSpan(0, 4), out var startYear)
                && startYear is > 1800 and < 2200
            )
                return startYear;

            var dashIndex = s.IndexOf('-');
            if (
                dashIndex > 0
                && int.TryParse(s[..dashIndex], out var year2)
                && year2 is > 1800 and < 2200
            )
                return year2;

            if (int.TryParse(s, out var plainYear) && plainYear is > 1800 and < 2200)
                return plainYear;
        }

        return null;
    }

    public DateTime? GetPremiereDate()
    {
        if (Released is { } dt)
            return dt;

        var year = GetYear();
        if (year is null)
        {
            return null;
        }
        return new DateTime(year.Value, 1, 1);
    }

    public bool IsValid()
    {
        return !Id.Contains("error");
    }

    public bool IsReleased(int bufferDays = 0)
    {
        var now = DateTime.UtcNow;

        // Check Released date first (most specific)
        if (Released.HasValue)
        {
            var homeReleaseDate = Released.Value.AddDays(bufferDays);
            return homeReleaseDate <= now;
        }

        // Check FirstAired for TV episodes
        if (FirstAired.HasValue)
        {
            return FirstAired.Value <= now;
        }

        if (Status is not null)
        {
            if (Status == StremioStatus.Upcoming)
            {
                return false;
            }
            if (Status == StremioStatus.Ended || Status == StremioStatus.Continuing)
            {
                return true;
            }
        }

        // Fall back to year-based check
        var year = GetYear();
        if (year.HasValue)
        {
            // For year-only dates, assume mid-year release + buffer
            var estimatedRelease = new DateTime(year.Value, 6, 1).AddDays(bufferDays);
            return estimatedRelease <= now;
        }

        // If we have no release information, assume it's not released
        return false;
    }
}

public class StremioTrailer
{
    public string? Source { get; set; }
    public string? Type { get; set; }
}

public class StremioLink
{
    public string? Name { get; set; }
    public string? Category { get; set; }
    public string? Url { get; set; }
}

public class StremioVideo
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public DateTime? Released { get; set; }
    public string? Thumbnail { get; set; }
    public int? Episode { get; set; }
    public int? Season { get; set; }
    public string? Overview { get; set; }
    public int? Number { get; set; }
    public string? Description { get; set; }
    public string? Rating { get; set; }
    public DateTime? FirstAired { get; set; }
}

public class StremioTrailerStream
{
    public string? Title { get; set; }
    public string? YtId { get; set; }
}

public class StremioAppExtras
{
    public List<StremioCast>? Cast { get; set; }
    public List<String?>? SeasonPosters { get; set; }
}

public class StremioCast
{
    public string? Name { get; set; }
    public string? Character { get; set; }
    public string? Photo { get; set; }
}

public class StremioStreamsResponse
{
    public List<StremioStream> Streams { get; set; } = new();

    public string? GetError()
    {
        var name = Streams.FirstOrDefault()?.GetName();
        return name is not null && name.Contains("error") ? name : null;
    }
}

public class StremioStream
{
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public string? Quality { get; set; }
    public string? Subtitle { get; set; }
    public string? Audio { get; set; }
    public string? InfoHash { get; set; }
    public int? FileIdx { get; set; }
    public List<string>? Sources { get; set; }
    public StremioBehaviorHints? BehaviorHints { get; set; }

    public string GetName()
    {
        if (!string.IsNullOrWhiteSpace(Title))
        {
            return Title;
        }
        return !string.IsNullOrWhiteSpace(Name) ? Name : "";
    }

    public Guid GetGuid()
    {
        string key;

        if (!string.IsNullOrEmpty(InfoHash))
        {
            key = InfoHash;
        }
        else if (
            !string.IsNullOrEmpty(BehaviorHints?.BingeGroup)
            && !string.IsNullOrEmpty(BehaviorHints?.Filename)
        )
        {
            key = $"{BehaviorHints?.BingeGroup}{BehaviorHints?.Filename}";
        }
        else
        {
            key = Url;
        }
        var bytes = System.Text.Encoding.UTF8.GetBytes(key);
        var hash = System.Security.Cryptography.MD5.HashData(bytes);
        return new Guid(hash);
    }

    public bool IsValid()
    {
        if (string.IsNullOrWhiteSpace(Url))
            return false;

        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri))
            return false;

        return !(uri.PathAndQuery == "/" || string.IsNullOrEmpty(uri.PathAndQuery));
    }

    public bool IsFile()
    {
        return !string.IsNullOrWhiteSpace(Url);
    }

    public bool IsTorrent()
    {
        return !string.IsNullOrWhiteSpace(InfoHash);
    }
}

public class StremioBehaviorHints
{
    public string? BingeGroup { get; set; }
    public string? VideoHash { get; set; }
    public long? VideoSize { get; set; }
    public string? Filename { get; set; }
    public bool Configurable { get; set; }
    public bool ConfigurationRequired { get; set; }
}

public class StremioOptions
{
    public string BaseUrl { get; set; } = "https://your-stremio-addon";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(8);
}

public enum StremioMediaType
{
    Unknown = 0,
    Movie,
    Series,
    Episode, // doesnt exist in stremio. But we wanna know
    Channel,
    Collections,
    Anime,
    Other,
    Tv,
    Events,
}

public enum StremioStatus
{
    Unknown = 0,
    Upcoming,
    Ended,
    Continuing,
}

// ReSharper restore UnusedAutoPropertyAccessor.Global
// ReSharper restore CollectionNeverUpdated.Global
// ReSharper restore ClassNeverInstantiated.Global
#endregion

public class SafeStringEnumConverter<T> : JsonConverter<T>
    where T : struct, Enum
{
    public override T Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options
    )
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (Enum.TryParse<T>(s, true, out var value))
                return value;
            if (Enum.TryParse<T>("Unknown", true, out var fallback))
                return fallback;
        }
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt32(out var i) && Enum.IsDefined(typeof(T), i))
                return (T)Enum.ToObject(typeof(T), i);
        }
        reader.Skip();
        return Enum.TryParse<T>("Unknown", true, out var fb) ? fb : default;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

public sealed class NullableIntLenientConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
    {
        switch (r.TokenType)
        {
            case JsonTokenType.Number:
                return r.TryGetInt32(out var i) ? i : null;
            case JsonTokenType.String:
                var s = r.GetString();
                if (string.IsNullOrWhiteSpace(s))
                    return null;

                return int.TryParse(
                    s,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var v
                )
                    ? v
                    : null;
            case JsonTokenType.Null:
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter w, int? v, JsonSerializerOptions o)
    {
        if (v.HasValue)
            w.WriteNumberValue(v.Value);
        else
            w.WriteNullValue();
    }
}
