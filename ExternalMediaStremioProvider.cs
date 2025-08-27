// StremioProvider.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Persistence;

namespace Jellyfin.Plugin.ExternalMedia
{
    public class ExternalMediaStremioProvider
    {
        private readonly IHttpClientFactory _http;
        private readonly ILogger<ExternalMediaStremioProvider> _log;
        private readonly StremioOptions _opts;
        private readonly IItemRepository _repo;

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public ExternalMediaStremioProvider(
            IHttpClientFactory http,
            ILogger<ExternalMediaStremioProvider> log,
            IItemRepository repo,
            StremioOptions opts)
        {
            _http = http;
            _log = log;
            _opts = opts;
        }

        public async Task<StremioMeta?> GetMetaAsync(string id, string type)
        {
            // var (type, extId) = ResolveKey(entity);
            // if (type is null || extId is null) return null;

            var url = $"{_opts.BaseUrl.TrimEnd('/')}/meta/{type}/{Uri.EscapeDataString(id)}.json";

            try
            {
                var c = _http.CreateClient(nameof(ExternalMediaStremioProvider));
                c.Timeout = _opts.Timeout;

                using var resp = await c.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return null;

                await using var s = await resp.Content.ReadAsStreamAsync();
                var r = await JsonSerializer.DeserializeAsync<StremioMetaResponse>(s, JsonOpts);
                return r?.Meta;
            }
            catch (Exception ex)
            {
                // _log.LogWarning(ex, "Meta fetch failed for {Type}/{ExtId}", type, extId);
                _log.LogWarning(ex, "Meta fetch failed for {Type}/{ExtId}", type, id);
                return null;
            }
        }

        public async Task<List<StremioStream>> GetStreamsAsync(BaseItem entity)
        {
            var (type, extId) = ResolveKey(entity);
            if (type is null || extId is null) return new();

            var url = $"{_opts.BaseUrl.TrimEnd('/')}/stream/{type}/{Uri.EscapeDataString(extId)}.json";

            try
            {
                var c = _http.CreateClient(nameof(ExternalMediaStremioProvider));
                c.Timeout = _opts.Timeout;

                using var resp = await c.GetAsync(url);
                if (!resp.IsSuccessStatusCode) return new();

                await using var s = await resp.Content.ReadAsStreamAsync();
                var r = await JsonSerializer.DeserializeAsync<StremioStreamsResponse>(s, JsonOpts);

                return r?.Streams ?? new();
            }
            catch (Exception ex)
            {
                // _log.LogDebug(ex, "Streams fetch failed for {Type}/{ExtId}", type, extId);
                _log.LogWarning("Streams fetch failed for {Type}/{ExtId}", type, extId);
                return new();
            }
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

            var imdb = entity.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb);
            if (!string.IsNullOrWhiteSpace(imdb)) return (type, imdb);

            var tmdb = entity.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb);
            if (!string.IsNullOrWhiteSpace(tmdb)) return (type, $"tmdb:{tmdb}");

            return (type, null);
        }


        public void ApplyMetaToEntity(BaseItem item, StremioMeta meta)
        {
            if (!string.IsNullOrWhiteSpace(meta.Description)) item.Overview = meta.Description;
            if (!string.IsNullOrWhiteSpace(meta.ImdbRating)) item.CommunityRating = (float)Convert.ToDouble(meta.ImdbRating);


            var images = new List<ItemImageInfo>();
            // if (!string.IsNullOrWhiteSpace(meta.Poster))
            // {
            //     var image = item.GetImages(ImageType.Primary).ToList().FirstOrDefault();
            //     if (image is not null)
            //     {
            //         image.Path = meta.Poster;
            //         images.Add(image);
            //     }
            // }

            // if (!string.IsNullOrWhiteSpace(meta.Background))
            // {
            //     var image = item.GetImages(ImageType.Backdrop).ToList().FirstOrDefault();
            //     if (image is not null)
            //     {
            //         image.Path = meta.Background;
            //         images.Add(image);
            //     }
            // }
            images.Add(GetImageOrOriginal(item, ImageType.Primary,  meta.Poster)!);
            images.Add(GetImageOrOriginal(item, ImageType.Backdrop, meta.Background)!);
            images.Add(GetImageOrOriginal(item, ImageType.Logo, meta.Logo)!);
            item.ImageInfos = images.ToArray();
        }

        private ItemImageInfo? GetImageOrOriginal(BaseItem item, ImageType type, string? url)
        {
            var image = item.GetImages(type).ToList().FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(url))
            {

                if (image is not null)
                {
                    image.Path = url;

                    //return image;
                }
            }
            _log.LogInformation("ExternalMedia: {K} pathh {J}", type, image?.Path);
            return image;
        }

        // private static void AttachExternalImages(BaseItem item, string imdbId, int? season = null, int? episode = null)
        // {
        //     var id = BuildStremioId(imdbId, season, episode);

        //     var images = new List<ItemImageInfo>
        //     {
        //         new ItemImageInfo { Path = BuildImageUrl(id, ImageType.Primary), Type = ImageType.Primary },
        //         new ItemImageInfo { Path = BuildImageUrl(id, ImageType.Logo), Type = ImageType.Logo },
        //         new ItemImageInfo { Path = BuildImageUrl(id, ImageType.Backdrop), Type = ImageType.Backdrop }
        //     };
        // }

    };




    public class StremioMetaResponse
    {
        public StremioMeta Meta { get; set; } = default!;
    }
    // replace the whole record with this class:
    public class StremioMeta
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public string? Poster { get; set; }
        public string? Background { get; set; }
        public string? Logo { get; set; }
        public string[]? Genres { get; set; }
        public string? Runtime { get; set; }
        public string? Imdb { get; set; }
        public string? Tmdb { get; set; }
        public string? ImdbRating { get; set; }
    }

    public class StremioStreamsResponse
    {
        public List<StremioStream> Streams { get; set; } = new();
    }
    // replace the whole record with this class:
    public class StremioStream
    {
        public string Url { get; set; } = "";
        public string? Title { get; set; }
        public string? Name { get; set; }
        public string? Quality { get; set; }
        public string? Subtitle { get; set; }
        public string? Audio { get; set; }
    }

    public class StremioOptions
    {
        public string BaseUrl { get; set; } = "https://your-stremio-addon";
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(8);
    }
}