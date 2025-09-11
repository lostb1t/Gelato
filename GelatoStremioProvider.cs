using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Gelato.Configuration;
using System.Text.Json.Serialization;
using Gelato.Common;

namespace Gelato
{
    public class GelatoStremioProvider
    {
        private readonly IHttpClientFactory _http;
        private readonly ILogger<GelatoStremioProvider> _log;
        private StremioManifest? _manifest;
        public StremioCatalog? MovieSearchCatalog { get; private set; }
        public StremioCatalog? SeriesSearchCatalog { get; private set; }
        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
        private readonly ILibraryManager _library;

        public GelatoStremioProvider(
                  ILibraryManager library,
            IHttpClientFactory http,
            ILogger<GelatoStremioProvider> log,
            MediaBrowser.Controller.Persistence.IItemRepository repo)
        {
            _http = http;
            _log = log;
            _library = library;
        }

        private HttpClient NewClient()
        {
            var c = _http.CreateClient(nameof(GelatoStremioProvider));
            c.Timeout = TimeSpan.FromSeconds(15);
            return c;
        }

        private string GetBaseUrlOrThrow()
        {
            var u = GelatoPlugin.Instance!.Configuration.GetBaseUrl()?.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(u)) throw new InvalidOperationException("Gelato Url not configured.");
            return u;
        }

        private string BuildUrl(string[] segments, IEnumerable<string>? extras = null)
        {
            var baseUrl = GetBaseUrlOrThrow();
            var parts = segments.Select(s => s == null ? "" : Uri.EscapeDataString(s)).ToArray();
            var path = string.Join("/", parts);
            var extrasPart = (extras != null && extras.Any()) ? "/" + string.Join("&", extras) : string.Empty;
            return $"{baseUrl}/{path}{extrasPart}.json";
        }

        private async Task<T?> GetJsonAsync<T>(string url)
        {
            try
            {
                using var c = NewClient();
                using var resp = await c.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return default;
                await using var s = await resp.Content.ReadAsStreamAsync();
                // var json = await resp.Content.ReadAsStringAsync();
                //_log.LogInformation("Gelato: Body {Json}", json);
                //  _log.LogInformation("Gelato: Response {StatusCode}", resp.StatusCode);
                return await JsonSerializer.DeserializeAsync<T>(s, JsonOpts);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "External fetch failed: {Url}", url);
                return default;
            }
        }


        public async Task<StremioManifest?> GetManifestAsync(bool force = false)
        {
            if (!force && _manifest is not null) return _manifest;

            var baseUrl = GetBaseUrlOrThrow();
            var url = $"{baseUrl}/manifest.json";
            var m = await GetJsonAsync<StremioManifest>(url);
            _manifest = m;

            if (m?.Catalogs != null)
            {
                MovieSearchCatalog = m.Catalogs.FirstOrDefault(c =>
                    c.Type == StremioMediaType.Movie &&
                    c.Extra != null &&
                    c.Extra.Any(e => string.Equals(e.Name, "search", StringComparison.OrdinalIgnoreCase)));

                SeriesSearchCatalog = m.Catalogs.FirstOrDefault(c =>
                    c.Type == StremioMediaType.Series &&
                    c.Extra != null &&
                    c.Extra.Any(e => string.Equals(e.Name, "search", StringComparison.OrdinalIgnoreCase)));
            }

            if (MovieSearchCatalog == null)
                _log.LogWarning("Gelato: manifest at {Url} has no search-capable movie catalog", url);

            if (SeriesSearchCatalog == null)
                _log.LogWarning("Gelato: manifest at {Url} has no search-capable series catalog", url);

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

        public async Task<List<StremioStream>> GetStreamsAsync(BaseItem item)
        {
            //var (mediaType, Id) = ResolveKey(entity);
            // _log.LogInformation("Gelato: GetStreamsAsync {Type} {Id}", mediaType, Id);
            var uri = StremioUri.FromString(item.GetProviderId("stremio"));
            //(mediaType is null || string.IsNullOrWhiteSpace(Id)) return new();
            var url = BuildUrl(new[] { "stream", uri.MediaType.ToString().ToLower(), uri.ExternalId });
            var r = await GetJsonAsync<StremioStreamsResponse>(url);
            return r?.Streams ?? new();
        }

        public async Task<List<StremioStream>> GetStreamsAsync(string Id, StremioMediaType mediaType)
        {
            // _log.LogInformation("Gelato: GetStreamsAsync {Type} {Id}", mediaType, Id);
            var url = BuildUrl(new[] { "stream", mediaType.ToString().ToLower(), Id });
            var r = await GetJsonAsync<StremioStreamsResponse>(url);
            return r?.Streams ?? new();
        }

        public async Task<IReadOnlyList<StremioMeta>> GetCatalogMetasAsync(
            string id,
            StremioMediaType mediaType,
            string? search = null,
            int? skip = null)
        {
            var extras = new List<string>();
            if (!string.IsNullOrWhiteSpace(search)) extras.Add($"search={Uri.EscapeDataString(search)}");
            if (skip is > 0) extras.Add($"skip={skip}");
            var url = BuildUrl(new[] { "catalog", mediaType.ToString().ToLower(), id }, extras);
            var r = await GetJsonAsync<StremioCatalogResponse>(url);
            return r?.Metas ?? new();
        }

        public async Task<IReadOnlyList<StremioMeta>> SearchAsync(
        string query,
        StremioMediaType mediaType,
        int? skip = null)
        {
            var manifest = await GetManifestAsync();
            if (manifest == null) return Array.Empty<StremioMeta>();

            StremioCatalog? catalog = mediaType switch
            {
                StremioMediaType.Movie => MovieSearchCatalog,
                StremioMediaType.Series => SeriesSearchCatalog,
                _ => null
            };

            if (catalog == null) return Array.Empty<StremioMeta>();

            return await GetCatalogMetasAsync(catalog.Id, mediaType, query, skip);
        }

        private static (string? type, string? extId) ResolveKey(BaseItem entity)
        {
            string? type = entity switch
            {
                Movie => "movie",
                Series => "series",
                Episode => "series",
                _ => null
            };

            var imdb = entity.GetProviderId(MetadataProvider.Imdb);
            if (!string.IsNullOrWhiteSpace(imdb)) return (type, imdb);

            var tmdb = entity.GetProviderId(MetadataProvider.Tmdb);
            if (!string.IsNullOrWhiteSpace(tmdb)) return (type, $"tmdb:{tmdb}");

            return (type, null);
        }

        public BaseItem IntoBaseItem(StremioMeta meta)
        {
            BaseItem item;

            var Id = meta.Id;

            // imdb is better
            if (!string.IsNullOrWhiteSpace(meta.ImdbId))
            {
                Id = meta.ImdbId;
            }

            switch (meta.Type)
            {
                case StremioMediaType.Series:
                    item = new Series
                    {
                        Id = _library.GetNewItemId(Id, typeof(Series)),
                        // Path = $"stremio://series/{Id}"
                    };
                    break;

                case StremioMediaType.Movie:
                    item = new Movie
                    {
                        Id = _library.GetNewItemId(Id, typeof(Movie)),
                        RunTimeTicks = Utils.ParseToTicks(meta.Runtime)
                    };

                    break;

                // case "episode":
                //     item = new Episode
                //    {
                //        Id = _library.GetNewItemId(Id, typeof(Episode)),
                //        RunTimeTicks = Utils.ParseToTicks(meta.Runtime)
                //    };
                //    break;
                default:
                    _log.LogWarning("Gelato: unsupported type {type}", meta.Type);
                    return null;
            }
            ;
            // item.Path = $"/media/test/{Id}";
            item.Name = meta.Name;
            if (!string.IsNullOrWhiteSpace(meta.Description)) item.Overview = meta.Description;
            //   if (!string.IsNullOrWhiteSpace(meta.ImdbRating)) item.CommunityRating = (float)Convert.ToDouble(meta.ImdbRating);
            if (!string.IsNullOrWhiteSpace(Id))
            {
                if (Id.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase))
                {
                    item.SetProviderId(MetadataProvider.Tmdb, Id.Substring("tmdb:".Length));
                }
                if (Id.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
                {
                    item.SetProviderId(MetadataProvider.Imdb, Id);
                }
            }

            item.SetProviderId("stremio", $"stremio://{meta.Type}/{Id}");

            // path is needed otherwise its set as placeholder and you cant play
            item.Path = $"stremio://{meta.Type}/{Id}".ToLower();
            item.IsVirtualItem = false;
            item.ProductionYear = meta.GetYear();
            item.PremiereDate = meta.GetPremiereDate();
            item.PresentationUniqueKey = item.CreatePresentationUniqueKey();

            if (!string.IsNullOrWhiteSpace(meta.Runtime))
                item.RunTimeTicks = Utils.ParseToTicks(meta.Runtime);

            if (!string.IsNullOrWhiteSpace(meta.Poster))
            {
                item.ImageInfos = new List<ItemImageInfo> {
                    new ItemImageInfo
                    {
                        Type = ImageType.Primary,
                        Path = meta.Poster,
                    }
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

    public class StremioMetaResponse
    {
        public StremioMeta Meta { get; set; } = default!;
    }

    public class StremioMeta
    {
        public string Id { get; set; } = "";
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
        public List<string>? Writer { get; set; }
        public string? Year { get; set; }
        public string? Slug { get; set; }
        public List<StremioTrailerStream>? TrailerStreams { get; set; }
        public StremioAppExtras? App_Extras { get; set; }
        public string? Thumbnail { get; set; }
        public int? Episode { get; set; }
        public int? Season { get; set; }
        public int? Number { get; set; }
        public DateTime? FirstAired { get; set; }

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

            if (int.TryParse(Year, out var y) && y is > 1800 and < 2200)
                return y;

            if (Released is DateTime dt)
                return dt.Year;

            // "2007-2019", "2020-", or "2015"
            if (!string.IsNullOrWhiteSpace(ReleaseInfo))
            {
                var s = ReleaseInfo.Trim();

                if (s.Length >= 4 && int.TryParse(s.AsSpan(0, 4), out var startYear) && startYear is > 1800 and < 2200)
                    return startYear;

                var dashIndex = s.IndexOf('-');
                if (dashIndex > 0 && int.TryParse(s[..dashIndex], out var year2) && year2 is > 1800 and < 2200)
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
    }

    public class StremioStream
    {
        public string Url { get; set; } = "";
        public string? Title { get; set; }
        public string? Name { get; set; }
        public string? Quality { get; set; }
        public string? Subtitle { get; set; }
        public string? Audio { get; set; }
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
            var size = BehaviorHints?.VideoSize?.ToString();
            var filename = BehaviorHints?.Filename;
            return !string.IsNullOrWhiteSpace(size)
                && !string.IsNullOrWhiteSpace(filename)
                && !string.IsNullOrWhiteSpace(GetName())
                && !string.IsNullOrWhiteSpace(Url);
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
        Events
    }
    
    public class SafeStringEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var s = reader.GetString();
            if (Enum.TryParse<T>(s, true, out var value)) return value;
            if (Enum.TryParse<T>("Unknown", true, out var fallback)) return fallback;
        }
        if (reader.TokenType == JsonTokenType.Number)
        {
            if (reader.TryGetInt32(out var i) && Enum.IsDefined(typeof(T), i)) return (T)Enum.ToObject(typeof(T), i);
        }
        reader.Skip();
        if (Enum.TryParse<T>("Unknown", true, out var fb)) return fb;
        return default;
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

}