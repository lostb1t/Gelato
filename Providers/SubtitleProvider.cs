#nullable enable
#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Gelato.Providers
{
    public sealed class SubtitleProvider : ISubtitleProvider
    {
        private readonly IHttpClientFactory _http;
        private readonly ILogger<SubtitleProvider> _log;
        private readonly ILibraryManager _library;
        private readonly ILocalizationManager _localization;

        private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

        // Static so the cache is shared across all instances — Jellyfin may create
        // provider instances via its own assembly scanning, separate from the DI container.
        private static readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

        public SubtitleProvider(
            IHttpClientFactory http,
            ILogger<SubtitleProvider> log,
            ILibraryManager library,
            ILocalizationManager localization
        )
        {
            _http = http;
            _log = log;
            _library = library;
            _localization = localization;
        }

        public string Name => "Gelato Subtitles";

        public IEnumerable<VideoContentType> SupportedMediaTypes =>
            new[] { VideoContentType.Movie, VideoContentType.Episode };

        /// <summary>
        /// Fetches the subtitle list for <paramref name="uri"/>, using <see cref="IMemoryCache"/>.
        /// On a cache miss the stremio endpoint is called and both the list and every individual
        /// subtitle entry are stored so <see cref="GetSubtitles"/> can serve them without a
        /// second network round-trip.
        /// </summary>
        public async Task<IReadOnlyList<StremioSubtitle>> GetSubtitlesAsync(
            string id,
            StremioMediaType mediaType,
            CancellationToken ct
        )
        {
            var listKey = ListCacheKey(id, mediaType);

            if (
                _cache.TryGetValue(listKey, out IReadOnlyList<StremioSubtitle>? cached)
                && cached is not null
            )
            {
                _log.LogDebug("Subtitle list cache HIT key={Key}", listKey);
                return cached;
            }

            _log.LogDebug("Subtitle list cache MISS key={Key}", listKey);

            var cfg = GelatoPlugin.Instance!.GetConfig(Guid.Empty);
            var subs = await cfg.Stremio!.GetSubtitlesAsync(id, mediaType).ConfigureAwait(false);

            _cache.Set(listKey, (IReadOnlyList<StremioSubtitle>)subs, CacheTtl);

            foreach (var s in subs)
                _cache.Set(SubCacheKey(PathSafeId(s.Id)), s, CacheTtl);

            return subs;
        }

        public async Task<IEnumerable<RemoteSubtitleInfo>> Search(
            SubtitleSearchRequest request,
            CancellationToken cancellationToken
        )
        {
            IReadOnlyList<StremioSubtitle> subs;
            try
            {
                var mediaType = MediaType(request);
                var stremioId = SearchId(request);

                if (string.IsNullOrEmpty(stremioId))
                {
                    _log.LogDebug(
                        "No Stremio ID, skipping subtitle search for {Path}",
                        Redact.Url(request.MediaPath)
                    );
                    return Array.Empty<RemoteSubtitleInfo>();
                }

                subs = await GetSubtitlesAsync(stremioId, mediaType, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(
                    ex,
                    "Subtitle search failed for {Path}",
                    Redact.Url(request.MediaPath)
                );
                return Array.Empty<RemoteSubtitleInfo>();
            }

            var lang = (request.TwoLetterISOLanguageName ?? "").Trim().ToLower();

            var filtered = (
                string.IsNullOrEmpty(lang)
                    ? subs
                    : subs.Where(s =>
                        string.Equals(
                            (s.TwoLetterISOLanguageName(_localization) ?? "").Trim().ToLower(),
                            lang,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
            ).ToList();

            _log.LogInformation(
                "Found: {Total} subtitles. After lang ({Lang}) filter: {Filtered}",
                subs.Count,
                lang,
                filtered.Count
            );

            // The file name only ranks what is left, and looking it up costs a query.
            if (filtered.Count == 0)
                return Array.Empty<RemoteSubtitleInfo>();

            // Derive release name from media path for title matching.
            // The SearchSubtitles decorator appends ".strm" so strip that first, then strip the real extension.
            var rawFilename = ReleaseFilename(request.MediaPath);
            if (rawFilename.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                rawFilename = rawFilename[..^5];
            var releaseName = Path.GetFileNameWithoutExtension(rawFilename);

            _log.LogDebug("Matching subtitles against release name: {ReleaseName}", releaseName);

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

            // Nothing is picked here: Jellyfin gets the whole list in this order.
            if (scored.Count > 0)
            {
                var top = scored[0];
                _log.LogInformation(
                    "Top ranked subtitle: '{Title}' (score={Score:F2} title={TitleScore:F2} bonus={Bonus:F2})",
                    top.Sub.Title ?? "(no title)",
                    top.Score,
                    top.TitleScore,
                    top.Bonus
                );
            }

            return scored.Select(x => new RemoteSubtitleInfo
            {
                Id = PathSafeId(x.Sub.Id),
                Name = Uri.UnescapeDataString(x.Sub.Title ?? ""),
                ProviderName = Name,
                Format = GuessSubtitleCodec(x.Sub.Url),
                ThreeLetterISOLanguageName = request.Language,
            });
        }

        private string ReleaseFilename(string mediaPath)
        {
            // Prefer the filename stored in GelatoData (set from BehaviorHints.Filename during stream
            // insertion), since the stream URL often doesn't contain a meaningful filename.
            // Stream rows are alternate versions, which Jellyfin leaves out of queries by default.
            var streamItem = _library
                .GetItemList(
                    new InternalItemsQuery { Path = mediaPath, IncludeOwnedItems = true }
                )
                .FirstOrDefault();
            var gelatoFilename = streamItem?.GelatoData<string>("filename");

            if (!string.IsNullOrEmpty(gelatoFilename))
            {
                _log.LogDebug("Using GelatoData filename: {Filename}", gelatoFilename);
                return gelatoFilename;
            }

            return
                Uri.TryCreate(mediaPath, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? Path.GetFileName(uri.LocalPath)
                : Path.GetFileName(mediaPath);
        }

        private static StremioMediaType MediaType(SubtitleSearchRequest request) =>
            request.ContentType == VideoContentType.Episode
                ? StremioMediaType.Series
                : StremioMediaType.Movie;

        /// <summary>The id the addon is asked about for a search, shared by all of a title's streams.</summary>
        public static string? SearchId(SubtitleSearchRequest request)
        {
            var imdbId = request.GetProviderId("ImdbId");
            if (imdbId == null)
                return request.GetProviderId("Stremio");

            return request.ContentType == VideoContentType.Episode
                ? $"{imdbId}:{request.ParentIndexNumber}:{request.IndexNumber}"
                : imdbId;
        }

        /// <summary>Whether the addon's answer for a search is cached: it answered, and recently.</summary>
        public static bool AddonAnswered(SubtitleSearchRequest request) =>
            SearchId(request) is { } id && _cache.TryGetValue(ListCacheKey(id, MediaType(request)), out _);

        public async Task<SubtitleResponse> GetSubtitles(
            string id,
            CancellationToken cancellationToken
        )
        {
            if (!_cache.TryGetValue(SubCacheKey(id), out StremioSubtitle sub))
            {
                _log.LogWarning("Subtitle cache miss/expired for id={Id}", id);
                throw new FileNotFoundException($"Subtitle not found for id {id}");
            }

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
                    Redact.Url(sub.Url),
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
                Language = sub.LangCode ?? sub.Lang,
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

            _log.LogWarning(
                "unkown subtitle format for {Path}, defaulting to srt",
                Redact.Url(urlOrPath)
            );
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

        private static string ListCacheKey(string id, StremioMediaType mediaType) =>
            $"gelato:subtitles:{mediaType.ToString().ToLower()}/{id}";

        private static string SubCacheKey(string id) => $"gelato:subtitle:{id}";

        /// <summary>
        /// The language a subtitle this provider listed is saved under, while it is cached.
        /// </summary>
        /// <param name="id">The id <see cref="Search"/> handed out.</param>
        public static string? CachedLanguage(string id) =>
            _cache.TryGetValue(SubCacheKey(id), out StremioSubtitle sub)
                ? sub.LangCode ?? sub.Lang
                : null;

        /// <summary>
        /// The id Jellyfin gets for a subtitle. AIOStreams builds its ids from release names, which
        /// can hold a slash (<c>720p/1080p</c>) or a backslash. The subtitle dialog's download sends
        /// the id as a route value, which ASP.NET Core leaves with <c>%2F</c> undecoded, so the
        /// addon's id would no longer match. A hash of it is safe in a path.
        /// </summary>
        private static string PathSafeId(string? id) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id ?? "")))[..32];
    }
}
