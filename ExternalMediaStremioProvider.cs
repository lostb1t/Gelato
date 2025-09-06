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
using Jellyfin.Plugin.ExternalMedia.Configuration;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ExternalMedia
{
    public class ExternalMediaStremioProvider
    {
        private readonly IHttpClientFactory _http;
        private readonly ILogger<ExternalMediaStremioProvider> _log;
        private StremioManifest? _manifest;
        public StremioCatalog? MovieSearchCatalog { get; private set; }
        public StremioCatalog? SeriesSearchCatalog { get; private set; }
        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
        private readonly ILibraryManager _library;

        public ExternalMediaStremioProvider(
                  ILibraryManager library,
            IHttpClientFactory http,
            ILogger<ExternalMediaStremioProvider> log,
            MediaBrowser.Controller.Persistence.IItemRepository repo)
        {
            _http = http;
            _log = log;
            _library = library;
        }

        private HttpClient NewClient()
        {
            var c = _http.CreateClient(nameof(ExternalMediaStremioProvider));
            c.Timeout = TimeSpan.FromSeconds(15);
            return c;
        }

        private string GetBaseUrlOrThrow()
        {
            var u = ExternalMediaPlugin.Instance!.Configuration.GetBaseUrl()?.Trim().TrimEnd('/');
            if (string.IsNullOrWhiteSpace(u)) throw new InvalidOperationException("ExternalMedia Url not configured.");
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
                //_log.LogInformation("ExternalMedia: Body {Json}", json);
                //  _log.LogInformation("ExternalMedia: Response {StatusCode}", resp.StatusCode);
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
                _log.LogWarning("ExternalMedia: manifest at {Url} has no search-capable movie catalog", url);

            if (SeriesSearchCatalog == null)
                _log.LogWarning("ExternalMedia: manifest at {Url} has no search-capable series catalog", url);

            return m;
        }

        public async Task<bool> IsReady()
        {
            // if manifest == null) return false;
            var manifest = await GetManifestAsync();
            return manifest != null;
        }

        public async Task<StremioMeta?> GetMetaAsync(string id, StremioMediaType mediaType)
        {
            var url = BuildUrl(new[] { "meta", mediaType.ToString().ToLower(), id });
            var r = await GetJsonAsync<StremioMetaResponse>(url);
            return r?.Meta;
        }

        public async Task<List<StremioStream>> GetStreamsAsync(BaseItem entity)
        {
            var (mediaType, Id) = ResolveKey(entity);
            // _log.LogInformation("ExternalMedia: GetStreamsAsync {Type} {Id}", mediaType, Id);
            if (mediaType is null || string.IsNullOrWhiteSpace(Id)) return new();
            var url = BuildUrl(new[] { "stream", mediaType.ToString().ToLower(), Id });
            var r = await GetJsonAsync<StremioStreamsResponse>(url);
            return r?.Streams ?? new();
        }

        public async Task<List<StremioStream>> GetStreamsAsync(string Id, StremioMediaType mediaType)
        {
            // _log.LogInformation("ExternalMedia: GetStreamsAsync {Type} {Id}", mediaType, Id);
            var url = BuildUrl(new[] { "stream", mediaType.ToString().ToLower(), Id });
            var r = await GetJsonAsync<StremioStreamsResponse>(url);
            return r?.Streams ?? new();
        }

        public async Task<IReadOnlyList<StremioMeta>> GetCatalogMetasAsync(
            string id,
            string mediaType,
            string? search = null,
            int? skip = null)
        {
            var extras = new List<string>();
            if (!string.IsNullOrWhiteSpace(search)) extras.Add($"search={Uri.EscapeDataString(search)}");
            if (skip is > 0) extras.Add($"skip={skip}");
            var url = BuildUrl(new[] { "catalog", mediaType, id }, extras);
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

            // type → "movie" | "series"
            var typeString = catalog.Type.ToString().ToLowerInvariant();
            return await GetCatalogMetasAsync(catalog.Id, typeString, query, skip);
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
                case "series":
                    item = new Series
                    {
                        Id = _library.GetNewItemId(Id, typeof(Series)),
                        // Path = $"stremio://series/{Id}"
                    };
                    break;

                case "movie":
                    item = new Movie
                    {
                        Id = _library.GetNewItemId(Id, typeof(Movie)),
                        // Path = $"stremio://movie/{Id}"
                    };
                    break;

                case "episode":
                    item = new Episode
                    {
                        Id = _library.GetNewItemId(Id, typeof(Episode)),
                        // Path = $"stremio://series/{Id}"
                    };
                    break;
                default:
                    _log.LogInformation("ExternalMedia: unsupported type {type}", meta.Type);
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
            //item.IsRemote = true;


            item.SetProviderId("stremio", $"stremio://{meta.Type}/{Id}");
            // item.IsVirtualItem = true;

            // path is needed otherwise its set as placeholder and you cant play
            item.Path = "stremio://{meta.Type}/{Id}";
            item.IsVirtualItem = false;
            // item.LocationType
            // item.RemoteTrailers =
            // item.CanDownload = true;
            // item.GetType
            //item.
            item.PresentationUniqueKey = item.CreatePresentationUniqueKey();
            var imgs = new List<ItemImageInfo?>();
            imgs.Add(UpdateImage(item, ImageType.Primary, meta.Poster));
            imgs.Add(UpdateImage(item, ImageType.Backdrop, meta.Background));
            imgs.Add(UpdateImage(item, ImageType.Logo, meta.Logo));
            item.ImageInfos = imgs.Where(i => i != null).Cast<ItemImageInfo>().ToArray();
            return item;
        }

        // public void MetaIntoBaseItem(BaseItem item, StremioMeta meta)
        // {
        //     if (!string.IsNullOrWhiteSpace(meta.Description)) item.Overview = meta.Description;
        //     if (!string.IsNullOrWhiteSpace(meta.ImdbRating)) item.CommunityRating = (float)Convert.ToDouble(meta.ImdbRating);

        //     var imgs = new List<ItemImageInfo?>();
        //     imgs.Add(UpdateImage(item, ImageType.Primary, meta.Poster));
        //     imgs.Add(UpdateImage(item, ImageType.Backdrop, meta.Background));
        //     imgs.Add(UpdateImage(item, ImageType.Logo, meta.Logo));
        //     item.ImageInfos = imgs.Where(i => i != null).Cast<ItemImageInfo>().ToArray();
        // }

        private ItemImageInfo? UpdateImage(BaseItem item, ImageType type, string? url)
        {
            var image = item.GetImages(type).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(url)) return image;
            if (image != null)
            {
                image.Path = url;
                return image;
            }
            return null;
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
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public StremioMediaType Type { get; set; } = StremioMediaType.Movie;
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public List<StremioExtra> Extra { get; set; } = new();
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
        public string Type { get; set; } = "";
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
        Movie,
        Series,
        Channel,
        Anime
    }

}