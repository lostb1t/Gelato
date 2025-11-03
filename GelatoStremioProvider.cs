using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Gelato.Common;
using Gelato.Configuration;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Gelato
{
    public class GelatoStremioProvider
    {
        private readonly IHttpClientFactory _http;
        private readonly ILogger<GelatoStremioProvider> _log;
        private StremioManifest? _manifest;
        public StremioCatalog? MovieSearchCatalog { get; private set; }
        public StremioCatalog? SeriesSearchCatalog { get; private set; }
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
        };
        private readonly ILibraryManager _library;

        public GelatoStremioProvider(
            ILibraryManager library,
            IHttpClientFactory http,
            ILogger<GelatoStremioProvider> log,
            MediaBrowser.Controller.Persistence.IItemRepository repo
        )
        {
            _http = http;
            _log = log;
            _library = library;
        }

        private HttpClient NewClient()
        {
            var c = _http.CreateClient(nameof(GelatoStremioProvider));
            c.Timeout = TimeSpan.FromSeconds(30);
            return c;
        }

        private string GetBaseUrlOrThrow()
        {
            var u = GelatoPlugin.Instance!.Configuration.GetBaseUrl()?.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(u))
                throw new InvalidOperationException("Gelato Url not configured.");
            return u;
        }

        private string BuildUrl(string[] segments, IEnumerable<string>? extras = null)
        {
            var baseUrl = GetBaseUrlOrThrow();
            var parts = segments.Select(s => s == null ? "" : Uri.EscapeDataString(s)).ToArray();
            var path = string.Join("/", parts);
            var extrasPart =
                (extras != null && extras.Any()) ? "/" + string.Join("&", extras) : string.Empty;
            var url = $"{baseUrl}/{path}{extrasPart}.json";
            url = url.Replace("%3A", ":").Replace("%3a", ":");
            // Console.Write(url);
            return url;
        }

        private async Task<T?> GetJsonAsync<T>(string url)
        {
            _log.LogDebug("GetJsonAsync: requesting {url}", url);
            //   try
            //   {
            using var c = NewClient();
            using var resp = await c.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug(
                    "external fetch failed: {Url} | status code: {StatusCode}",
                    url,
                    resp.StatusCode
                );
                throw new HttpRequestException(
                    $"HTTP {resp.StatusCode}: {resp.ReasonPhrase}",
                    null,
                    resp.StatusCode
                );
            }
            await using var s = await resp.Content.ReadAsStreamAsync();
            return await JsonSerializer.DeserializeAsync<T>(s, JsonOpts);
        }

        public async Task<StremioManifest?> GetManifestAsync(bool force = false)
        {
            if (!force && _manifest is not null)
                return _manifest;

            var baseUrl = GetBaseUrlOrThrow();
            var url = $"{baseUrl}/manifest.json";
            var m = await GetJsonAsync<StremioManifest>(url);
            _manifest = m;

            if (m?.Catalogs != null)
            {
                MovieSearchCatalog = m
                    .Catalogs.Where(c =>
                        c.Type == StremioMediaType.Movie
                        && c.Extra?.Any(e =>
                            string.Equals(e.Name, "search", StringComparison.OrdinalIgnoreCase)
                        ) == true
                    )
                    .OrderByDescending(c =>
                        c.Id?.EndsWith(".tmdb.search", StringComparison.OrdinalIgnoreCase) == true
                    )
                    .FirstOrDefault();

                SeriesSearchCatalog = m
                    .Catalogs.Where(c =>
                        c.Type == StremioMediaType.Series
                        && c.Extra?.Any(e =>
                            string.Equals(e.Name, "search", StringComparison.OrdinalIgnoreCase)
                        ) == true
                    )
                    .OrderByDescending(c =>
                        c.Id?.EndsWith(".tmdb.search", StringComparison.OrdinalIgnoreCase) == true
                    )
                    .FirstOrDefault();
            }

            if (MovieSearchCatalog == null)
            {
                _log.LogWarning("manifest has no search-capable movie catalog", url);
            }
            else
            {
                _log.LogInformation(
                    "manifest uses movie search catalog: {Id}",
                    MovieSearchCatalog.Id
                );
            }

            if (SeriesSearchCatalog == null)
            {
                _log.LogWarning("manifest has no search-capable series catalog", url);
            }
            else
            {
                _log.LogInformation(
                    "manifest uses series search catalog: {Id}",
                    SeriesSearchCatalog.Id
                );
            }
            return m;
        }

        public async Task<bool> IsReady()
        {
            try
            {
                var manifest = await GetManifestAsync();
                return manifest != null;
            }
            catch
            {
                return false;
            }
        }

        public async Task<StremioMeta?> GetMetaAsync(string id, StremioMediaType mediaType)
        {
            var url = BuildUrl(new[] { "meta", mediaType.ToString().ToLower(), id });
            var r = await GetJsonAsync<StremioMetaResponse>(url);
            return r?.Meta;
        }

        public async Task<StremioMeta?> GetMetaAsync(BaseItem item)
        {
            var id = item.GetProviderId("Imdb");
            if (id is null)
            {
                _log.LogWarning("GetMetaAsync: {Name} has no imdb ID", item.Name);
                // return null;
                id = item.GetProviderId("Tmdb");
                if (id is null)
                {
                    _log.LogWarning("GetMetaAsync: {Name} has no imdb and tmdb ID", item.Name);
                    return null;
                }
                id = $"tmdb:{id}";
            }
            ;
            var url = BuildUrl(
                new string[] { "meta", item.GetBaseItemKind().ToStremio().ToString().ToLower(), id }
            );
            var r = await GetJsonAsync<StremioMetaResponse>(url);
            return r?.Meta;
        }

        public async Task<List<StremioStream>> GetStreamsAsync(StremioUri uri)
        {
            return await GetStreamsAsync(uri.ExternalId, uri.MediaType);
        }

        public async Task<List<StremioStream>> GetStreamsAsync(
            string id,
            StremioMediaType mediaType
        )
        {
            var url = BuildUrl(new[] { "stream", mediaType.ToString().ToLower(), id });
            var r = await GetJsonAsync<StremioStreamsResponse>(url);

            var error = r?.GetError();
            if (error is not null)
            {
                throw new InvalidOperationException($"Stremio returned an error: {error}");
            }

            return r?.Streams ?? new();
        }

        public async Task<List<StremioSubtitle>> GetSubtitlesAsync(StremioUri uri, string? fileName)
        {
            string[] extras = Array.Empty<string>();

            if (!string.IsNullOrEmpty(fileName))
            {
                extras = new[] { $"filename={fileName}" };
            }

            var url = BuildUrl(
                new[] { "subtitles", uri.MediaType.ToString().ToLower(), uri.ExternalId },
                extras
            );
            var r = await GetJsonAsync<StremioSubtitleResponse>(url);
            return r.Subtitles;
        }

        public async Task<IReadOnlyList<StremioMeta>> GetCatalogMetasAsync(
            string id,
            StremioMediaType mediaType,
            string? search = null,
            int? skip = null
        )
        {
            var extras = new List<string>();
            if (!string.IsNullOrWhiteSpace(search))
                extras.Add($"search={Uri.EscapeDataString(search)}");
            if (skip is > 0)
                extras.Add($"skip={skip}");
            var url = BuildUrl(new[] { "catalog", mediaType.ToString().ToLower(), id }, extras);
            var r = await GetJsonAsync<StremioCatalogResponse>(url);
            return r?.Metas ?? new();
        }

        public async Task<IReadOnlyList<StremioMeta>> SearchAsync(
            string query,
            StremioMediaType mediaType,
            int? skip = null
        )
        {
            var manifest = await GetManifestAsync();
            if (manifest == null)
                return Array.Empty<StremioMeta>();

            StremioCatalog? catalog = mediaType switch
            {
                StremioMediaType.Movie => MovieSearchCatalog,
                StremioMediaType.Series => SeriesSearchCatalog,
                _ => null,
            };

            if (catalog == null)
            {
                _log.LogError("SearchAsync: {mediaType} has no search catalog", mediaType);
                return Array.Empty<StremioMeta>();
            }
            ;

            return await GetCatalogMetasAsync(catalog.Id, mediaType, query, skip);
        }

        private static (string? type, string? extId) ResolveKey(BaseItem entity)
        {
            string? type = entity switch
            {
                Movie => "movie",
                Series => "series",
                Episode => "series",
                _ => null,
            };

            var imdb = entity.GetProviderId(MetadataProvider.Imdb);
            if (!string.IsNullOrWhiteSpace(imdb))
                return (type, imdb);

            var tmdb = entity.GetProviderId(MetadataProvider.Tmdb);
            if (!string.IsNullOrWhiteSpace(tmdb))
                return (type, $"tmdb:{tmdb}");

            return (type, null);
        }

        public BaseItem IntoBaseItem(StremioMeta meta)
        {
            BaseItem item;

            var Id = meta.Id;

            switch (meta.Type)
            {
                case StremioMediaType.Series:
                    item = new Series
                    {
                        Id = meta.Guid ?? _library.GetNewItemId(Id, typeof(Series)),
                    };
                    break;

                case StremioMediaType.Movie:
                    item = new Movie { Id = meta.Guid ?? _library.GetNewItemId(Id, typeof(Movie)) };

                    break;

                // case "episode":
                //     item = new Episode
                //    {
                //        Id = _library.GetNewItemId(Id, typeof(Episode)),
                //        RunTimeTicks = Utils.ParseToTicks(meta.Runtime)
                //    };
                //    break;
                default:
                    _log.LogWarning("unsupported type {type}", meta.Type);
                    return null;
            }
            ;
            item.Name = meta.Name;
            if (!string.IsNullOrWhiteSpace(meta.Runtime))
                item.RunTimeTicks = Utils.ParseToTicks(meta.Runtime);
            if (!string.IsNullOrWhiteSpace(meta.Description))
                item.Overview = meta.Description;

            // NOTICE: do this only for show and movie. cause the parent imdb is used for season abd episodes
            if (!string.IsNullOrWhiteSpace(Id))
            {
                var providerMappings = new (string Prefix, string Provider, bool StripPrefix)[]
                {
                    ("tmdb:", MetadataProvider.Tmdb.ToString(), true),
                    ("tt", MetadataProvider.Imdb.ToString(), false),
                    ("anidb:", "AniDB", true),
                    ("kitsu:", "Kitsu", true),
                    ("mal:", "Mal", true),
                    ("anilist:", "Anilist", true),
                    ("tvdb:", MetadataProvider.Tvdb.ToString(), true),
                    ("tvmaze:", MetadataProvider.TvMaze.ToString(), true),
                };

                foreach (var (prefix, provider, stripPrefix) in providerMappings)
                {
                    if (Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        var providerId = stripPrefix ? Id.Substring(prefix.Length) : Id;
                        item.SetProviderId(provider, providerId);
                        break;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(meta.ImdbId))
            {
                item.SetProviderId(MetadataProvider.Imdb, meta.ImdbId);
            }

            var stremioUri = new StremioUri(meta.Type, meta.ImdbId ?? Id);
            item.SetProviderId("Stremio", stremioUri.ExternalId);

            // path is needed otherwise its set as placeholder and you cant play
            item.Path = stremioUri.ToString();
            item.IsVirtualItem = false;
            item.ProductionYear = meta.GetYear();
            item.PremiereDate = meta.GetPremiereDate();
            item.PresentationUniqueKey = item.CreatePresentationUniqueKey();
            item.Overview = meta.Description;

            if (!string.IsNullOrWhiteSpace(meta.Runtime))
                item.RunTimeTicks = Utils.ParseToTicks(meta.Runtime);

            if (!string.IsNullOrWhiteSpace(meta.Poster))
            {
                item.ImageInfos = new List<ItemImageInfo>
                {
                    new ItemImageInfo { Type = ImageType.Primary, Path = meta.Poster },
                }.ToArray();
            }
            return item;
        }
    }

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
        [JsonConverter(typeof(SafeStringEnumConverter<StremioMediaType>))]
        public StremioMediaType Type { get; set; } = StremioMediaType.Unknown;
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<StremioExtra> Extra { get; set; } = new();

        public bool IsSearchCapable()
        {
            return Extra.Any(e =>
                string.Equals(e.Name, "search", StringComparison.OrdinalIgnoreCase)
            );
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
        public StremioMeta Meta { get; set; } = default!;
    }

    public class StremioMeta
    {
        public string? Id { get; set; }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public StremioMediaType Type { get; set; } = StremioMediaType.Unknown;
        public string Name { get; set; } = "";
        public string? Poster { get; set; }
        public List<string>? Genres { get; set; }
        public string? ImdbRating { get; set; }
        public string? ReleaseInfo { get; set; }
        public string? Description { get; set; }
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
        public string? Status { get; set; }

        [JsonConverter(typeof(NullableIntLenientConverter))]
        public int? Year { get; set; }
        public string? Slug { get; set; }
        public List<StremioTrailerStream>? TrailerStreams { get; set; }
        public StremioAppExtras? App_Extras { get; set; }
        public string? Thumbnail { get; set; }
        public int? Episode { get; set; }
        public int? Season { get; set; }
        public int? Number { get; set; }
        public DateTime? FirstAired { get; set; }
        public Guid? Guid { get; set; }

        public Dictionary<string, string> GetProviderIds()
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(Id))
            {
                if (Id.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase))
                {
                    dict[MetadataProvider.Tmdb.ToString()] = Id.Substring("tmdb:".Length);
                }
                else if (Id.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                {
                    dict[MetadataProvider.Imdb.ToString()] = Id;
                }
            }

            if (!string.IsNullOrWhiteSpace(ImdbId))
            {
                dict[MetadataProvider.Imdb.ToString()] = ImdbId;
            }

            return dict;
        }

        public int? GetYear()
        {
            if (Year is not null)
                return Year;

            if (Released is DateTime dt)
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
            var year = GetYear();
            if (year is null)
            {
                return null;
            }
            return new DateTime(year.Value, 1, 1);
        }

        public bool IsValid()
        {
            if (Id is not null && !Id.Contains("error"))
            {
                return true;
            }

            return false;
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
            if (!string.IsNullOrWhiteSpace(Name))
            {
                return Name;
            }
            return "";
        }

        public Guid GetGuid()
        {
            //var size = BehaviorHints?.VideoSize?.ToString() ?? "0";
            var filename = BehaviorHints?.Filename ?? string.Empty;
            var bingeGroup = BehaviorHints?.BingeGroup ?? string.Empty;
            var key = $"{bingeGroup}{filename}";

            using var md5 = System.Security.Cryptography.MD5.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(key);
            var hash = md5.ComputeHash(bytes);
            return new Guid(hash);
        }

        public string GetKey()
        {
            var filename = BehaviorHints?.Filename ?? string.Empty;
            var bingeGroup = BehaviorHints?.BingeGroup ?? string.Empty;
            return $"{bingeGroup}{filename}";
        }

        public string GetShortId()
        {
            var key = GetKey();
            using var md5 = System.Security.Cryptography.MD5.Create();
            var bytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(key));
            return BitConverter.ToUInt32(bytes, 0).ToString("X8");
        }

        public bool IsValid()
        {
            //var size = BehaviorHints?.VideoSize?.ToString();
            //var bingeGroup = BehaviorHints?.BingeGroup ?? string.Empty;
            var filename = BehaviorHints?.Filename;
            return !string.IsNullOrWhiteSpace(filename)
                // && !string.IsNullOrWhiteSpace(size)
                && !string.IsNullOrWhiteSpace(GetName())
                && (!string.IsNullOrWhiteSpace(Url) || !string.IsNullOrWhiteSpace(InfoHash));
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
        Channel,
        Collections,
        Anime,
        Other,
        Tv,
        Events,
    }

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
            if (Enum.TryParse<T>("Unknown", true, out var fb))
                return fb;
            return default;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    public sealed class NullableIntLenientConverter : JsonConverter<int?>
    {
        public override int? Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            if (r.TokenType == JsonTokenType.Number)
                return r.TryGetInt32(out var i) ? i : (int?)null;

            if (r.TokenType == JsonTokenType.String)
            {
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
                    : (int?)null;
            }

            if (r.TokenType == JsonTokenType.Null)
                return null;

            return null;
        }

        public override void Write(Utf8JsonWriter w, int? v, JsonSerializerOptions o)
        {
            if (v.HasValue)
                w.WriteNumberValue(v.Value);
            else
                w.WriteNullValue();
        }
    }
}
