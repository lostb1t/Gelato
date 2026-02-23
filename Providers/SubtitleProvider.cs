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

        public SubtitleProvider(IHttpClientFactory http, ILogger<SubtitleProvider> log)
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
            string filename = string.Empty;
            try
            {
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

                subs = await cfg
                    .Stremio.GetSubtitlesAsync(stremioUri, filename)
                    .ConfigureAwait(false);
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

            var filtered = (
                string.IsNullOrEmpty(lang)
                    ? subs
                    : subs.Where(s =>
                        string.Equals(
                            (s.LangCode ?? "").Trim().ToLower(),
                            lang,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
            ).ToList();

            _log.LogInformation(
                "Found: {Total} subtitles. After lang filter: {Filtered}",
                subs.Count,
                filtered.Count
            );

            // Derive release name from media path for title matching.
            // The SearchSubtitles decorator appends ".strm" so strip that first, then strip the real extension.
            var rawFilename = filename;
            if (rawFilename.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                rawFilename = rawFilename[..^5];
            var releaseName = Path.GetFileNameWithoutExtension(rawFilename);

            _log.LogInformation(
                "Matching subtitles against release name: {ReleaseName}",
                releaseName
            );

            var scored = filtered
                .Select(s =>
                {
                    var titleScore = TitleMatchScore(releaseName, s.Title);
                    var trusted = s.FromTrusted == true ? 0.2 : 0.0;
                    var notAi = s.AiTranslated == false ? 0.1 : 0.0;
                    return (
                        Sub: s,
                        TitleScore: titleScore,
                        Bonus: trusted + notAi,
                        Score: titleScore + trusted + notAi
                    );
                })
                .OrderByDescending(x => x.Score)
                .ToList();

            foreach (var (s, titleScore, bonus, score) in scored)
                _log.LogDebug(
                    "  [{Score:F2}] title={TitleScore:F2} bonus={Bonus:F2} trusted={Trusted} ai={Ai} '{Title}'",
                    score,
                    titleScore,
                    bonus,
                    s.FromTrusted,
                    s.AiTranslated,
                    s.Title ?? "(no title)"
                );

            var bestIdx = scored.FindIndex(x => x.Score > 0.4);
            if (bestIdx >= 0)
            {
                var best = scored[bestIdx];
                _log.LogInformation(
                    "Best subtitle match: '{Title}' (score={Score:F2} title={TitleScore:F2} bonus={Bonus:F2})",
                    best.Sub.Title ?? "(no title)",
                    best.Score,
                    best.TitleScore,
                    best.Bonus
                );
            }
            else if (scored.Count > 0)
                _log.LogInformation(
                    "No title match above threshold, using first: '{Title}'",
                    scored[0].Sub.Title ?? "(no title)"
                );

            return scored.Select(x => new RemoteSubtitleInfo
            {
                Id = x.Sub.Id,
                Name = x.Sub.Title,
                ProviderName = Name,
                Format = GuessSubtitleCodec(x.Sub.Url),
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

        private static double TitleMatchScore(string releaseName, string? title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return 0;

            var a = TokenizeReleaseName(releaseName);
            var b = TokenizeReleaseName(title);

            if (a.Count == 0 || b.Count == 0)
                return 0;

            var intersection = a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count();
            var union = a.Union(b, StringComparer.OrdinalIgnoreCase).Count();
            return union == 0 ? 0 : (double)intersection / union;
        }

        private static HashSet<string> TokenizeReleaseName(string s) =>
            s.Split(
                    new[] { '.', '-', '_', ' ', '(', ')', '[', ']' },
                    StringSplitOptions.RemoveEmptyEntries
                )
                .Select(t => t.ToLowerInvariant())
                .ToHashSet();

        private static string CacheKey(string id) => $"gelato:subtitle:{id}";
    }
}
