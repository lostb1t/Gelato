#nullable enable
#pragma warning disable CS1591

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Gelato.Providers
{
    public sealed class SubtitleProvider : ISubtitleProvider
    {
        private readonly IHttpClientFactory _http;
        private readonly ILogger<SubtitleProvider> _log;

        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

        // Replacement for IMemoryCache: simple TTL store
        private static readonly ConcurrentDictionary<
            string,
            (StremioSubtitle Sub, DateTime Expires)
        > _subsCache = new();

        public SubtitleProvider(
            IHttpClientFactory http,
            ILogger<SubtitleProvider> log
        )
        {
            _http = http;
            _log = log;
        }

        public string Name => "Gelato Subtitles";

        public IEnumerable<VideoContentType> SupportedMediaTypes =>
            new[] { VideoContentType.Movie, VideoContentType.Episode };

        public async Task<IEnumerable<RemoteSubtitleInfo>> Search(
            SubtitleSearchRequest request,
            CancellationToken cancellationToken
        )
        {
            IReadOnlyList<StremioSubtitle> subs;
            var cfg = GelatoPlugin.Instance!.GetConfig(Guid.Empty);
            try
            {
                string filename;

                if (
                    Uri.TryCreate(request.MediaPath, UriKind.Absolute, out var uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                )
                {
                    filename = Path.GetFileName(uri.LocalPath);
                }
                else
                {
                    filename = Path.GetFileName(request.MediaPath);
                }

                var imdb = request.ProviderIds["Imdb"];
                var stremioUri = new StremioUri(StremioMediaType.Movie, imdb);

                subs = await cfg.Stremio.GetSubtitlesAsync(stremioUri, filename).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Subtitle search failed for {Path}", request.MediaPath);
                return Array.Empty<RemoteSubtitleInfo>();
            }

            var now = DateTime.UtcNow;
            foreach (var s in subs)
            {
                // _log.LogDebug($"s: {s.LangCode} id: {s.Id}");
                _subsCache[CacheKey(s.Id)] = (s, now + CacheTtl);
            }

            var lang = (request.TwoLetterISOLanguageName ?? "").Trim().ToLower();

            var filtered = string.IsNullOrEmpty(lang)
                ? subs
                : subs.Where(s =>
                    string.Equals(
                        (s.LangCode ?? "").Trim().ToLower(),
                        lang,
                        StringComparison.OrdinalIgnoreCase
                    )
                );
            _log.LogDebug($"Found: {subs.Count} subtitles. After filter {filtered.Count()}");
            return subs.Select(s => new RemoteSubtitleInfo
            {
                Id = s.Id,
                Name = s.Title,
                ProviderName = Name,
                Format = GuessSubtitleCodec(s.Url),
                ThreeLetterISOLanguageName = request.Language,
            });
        }

        public async Task<SubtitleResponse> GetSubtitles(
            string id,
            CancellationToken cancellationToken
        )
        {
            var key = CacheKey(id);

            if (!_subsCache.TryGetValue(key, out var entry) || entry.Expires <= DateTime.UtcNow)
            {
                _subsCache.TryRemove(key, out _);
                _log.LogWarning("Subtitle cache miss/expired for id={Id}", id);
                throw new FileNotFoundException($"Subtitle not found for id {id}");
            }

            var sub = entry.Sub;

            var client = _http.CreateClient(nameof(SubtitleProvider));
            using var resp = await client.GetAsync(
                sub.Url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken
            );
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogError(
                    "Failed to download subtitle id={Id} from {Url}. Status={Status}",
                    id,
                    sub.Url,
                    resp.StatusCode
                );
                throw new IOException($"Failed to download subtitles: {resp.StatusCode}");
            }

            var ms = new MemoryStream();
            await (await resp.Content.ReadAsStreamAsync(cancellationToken)).CopyToAsync(
                ms,
                cancellationToken
            );
            ms.Position = 0;

            return new SubtitleResponse
            {
                Format = GuessSubtitleCodec(sub.Url),
                Stream = ms,
                Language = "en",
            };
        }

        public string GuessSubtitleCodec(string? urlOrPath)
        {
            if (string.IsNullOrWhiteSpace(urlOrPath))
                return "subrip";

            var s = urlOrPath.ToLowerInvariant();

            if (s.Contains(".vtt"))
                return "vtt";
            if (s.Contains(".srt"))
                return "srt";
            if (s.Contains(".ass") || s.Contains(".ssa"))
                return "ass";
            if (s.Contains(".subf2m"))
                return "subrip";
            if (s.Contains("subs") && s.Contains(".strem.io"))
                return "srt"; // Stremio proxies are always normalized to .srt

            _log.LogWarning("unkown subtitle format for {Path}, defaulting to srt", s);
            return "srt";
        }

        private static string CacheKey(string id) => $"gelato:subtitle:{id}";
    }
}
