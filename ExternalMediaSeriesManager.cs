using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ExternalMedia.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.ExternalMedia
{
    public partial class ExternalMediaSeriesManager
    {
        private readonly IFileSystem _fs;
        private readonly ILogger<ExternalMediaSeriesManager> _log;
        private readonly ExternalMediaManager _manager;

        public ExternalMediaSeriesManager(IFileSystem fs, ILogger<ExternalMediaSeriesManager> log, ExternalMediaManager manager)
        {
            _fs = fs;
            _log = log;
            _manager = manager;
        }

        // public string GetSeasonFolderName(StremioMeta meta)
        // {
        //     // var seriesFolderName = GetSeriesFolderName(meta);
        //     return $"Season {group.Key:D2}";
        // }

        /// <summary>
        /// Builds Series/Season/Episode structure from Stremio meta and writes one .strm per episode
        /// with a placeholder URL. No network calls.
        /// Series folder name format: "Title (Year) [imdbid-ttxxxxxx] [tmdbid-yyyyy] …"
        /// Returns true if at least one episode file was created.
        /// </summary>
        public async Task<bool> CreateSeriesTreesAsync(
            Folder seriesRootFolder,
            StremioMeta seriesMeta,
            // bool addPlaceholders,
            CancellationToken ct)
        {
            if (seriesRootFolder is null || string.IsNullOrWhiteSpace(seriesRootFolder.Path))
            {
                _log.LogWarning("SaveSeriesPlaceholdersAsync: invalid series root folder");
                return false;
            }

            // var title = seriesMeta.Name;
            // if (string.IsNullOrWhiteSpace(title))
            // {
            //     _log.LogWarning("SaveSeriesPlaceholdersAsync: missing series title");
            //     return false;
            // }

            // var year = ExtractYear(seriesMeta);
            // var providerIds = seriesMeta.GetProviderIds(); // always non-null
            var seriesFolderName = GetSeriesFolderName(seriesMeta);

            var seriesPath = Path.Combine(seriesRootFolder.Path, seriesFolderName);
            Directory.CreateDirectory(seriesPath);

            var episodes = (seriesMeta.Videos ?? Enumerable.Empty<StremioMeta>())
                .OrderBy(e => e.Season)
                .ThenBy(e => e.Episode)
                .ToList();

            if (episodes.Count == 0)
            {
                _log.LogWarning("SaveSeriesPlaceholdersAsync: series has no episodes in meta; aborting");
                return false;
            }

            int created = 0;

            foreach (var group in episodes.GroupBy(e => e.Season))
            {
                ct.ThrowIfCancellationRequested();

                var seasonFolder = Path.Combine(seriesPath, $"Season {group.Key:D2}");
                if (Directory.Exists(seasonFolder))
                {
                    _log.LogDebug("SaveSeriesPlaceholdersAsync: season folder already exists; skipping");
                    continue;
                }

                Directory.CreateDirectory(seasonFolder);

                foreach (var ep in group)
                {
                    ct.ThrowIfCancellationRequested();

                    var baseName = $"{SanitizeForPath(seriesFolderName)} S{ep.Season:D2}E{ep.Episode:D2}";
                    var epPath = Path.Combine(seasonFolder, baseName + ".strm");

                    // Placeholder only; resolved later at playback by your resolver
                    var placeholderUrl = BuildEpisodePlaceholderUrl(seriesMeta, ep);

                    await _manager.WriteAllTextAsync(epPath, placeholderUrl, ct).ConfigureAwait(false);
                    created++;
                }
            }

            // _log.LogInformation(
            //     "SaveSeriesPlaceholdersAsync: created {Count} placeholder .strm files for '{Title}' at {SeriesPath}",
            //     created, title, seriesPath);

            return created > 0;
        }

        private static string BuildEpisodePlaceholderUrl(StremioMeta seriesMeta, StremioMeta ep)
        {
            // Intentionally non-playable custom scheme; your controller can resolve this at playback time.
            // Example: externalmedia://resolve?seriesId=tmdb:120&s=1&e=2&epId=tmdb:120:1:2
            var seriesId = seriesMeta.Id ?? "";
            var epId = ep.Id ?? "";

            var sb = new StringBuilder("externalmedia://resolve?");
            sb.Append("seriesId=").Append(Uri.EscapeDataString(seriesId));
            sb.Append("&s=").Append(ep.Season);
            sb.Append("&e=").Append(ep.Episode);
            if (!string.IsNullOrWhiteSpace(epId))
                sb.Append("&epId=").Append(Uri.EscapeDataString(epId));
            return sb.ToString();
        }

        // ---------- helpers ----------

        private readonly record struct EpisodeShape(int Season, int Episode, string? Name, string? id, StremioMeta Meta);

        /// <summary>
        /// "Title (Year) [imdbid-ttxxxxxx] [tmdbid-yyyyy] [otherid-...]" (year and tags optional).
        /// IMDb then TMDb, then other providers (alphabetical by provider key).
        /// </summary>
        public string GetSeriesFolderName(StremioMeta meta)
        {
            var title = meta.Name;
            var year = ExtractYear(meta);
            var providerIds = meta.GetProviderIds();
            var cleanTitle = SanitizeForPath(title);
            var sb = new StringBuilder(cleanTitle);

            if (year.HasValue)
            {
                sb.Append(' ').Append('(').Append(year.Value).Append(')');
            }

            // Build stable, ordered list of external ID tags
            foreach (var tag in BuildExternalIdTags(providerIds))
            {
                sb.Append(' ').Append('[').Append(tag).Append(']');
            }

            return sb.ToString();
        }

        /// <summary>
        /// Builds tags like "imdbid-tt1234567", "tmdbid-120", then any other providers sorted by key.
        /// </summary>
        private static IEnumerable<string> BuildExternalIdTags(Dictionary<string, string> providerIds)
        {
            if (providerIds == null || providerIds.Count == 0)
                yield break;

            // Normalize lookups
            static bool TryGet(Dictionary<string, string> d, string key, out string value)
                => d.TryGetValue(key, out value)
                || d.TryGetValue(key.ToUpperInvariant(), out value)
                || d.TryGetValue(key.ToLowerInvariant(), out value);

            // Priority 1: IMDb
            if (TryGet(providerIds, MetadataProvider.Imdb.ToString(), out var imdb) && !string.IsNullOrWhiteSpace(imdb))
                yield return "imdbid-" + SanitizeTagValue(imdb);

            // Priority 2: TMDb
            if (TryGet(providerIds, MetadataProvider.Tmdb.ToString(), out var tmdb) && !string.IsNullOrWhiteSpace(tmdb))
                yield return "tmdbid-" + SanitizeTagValue(tmdb);

            // Others: alphabetical by provider key, exclude imdb/tmdb duplicates
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                MetadataProvider.Imdb.ToString(),
                MetadataProvider.Tmdb.ToString()
            };

            foreach (var kv in providerIds
                .Where(kv => !excluded.Contains(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            {
                var keyLower = kv.Key.Replace(" ", "", StringComparison.Ordinal).ToLowerInvariant();
                yield return $"{keyLower}id-{SanitizeTagValue(kv.Value)}";
            }
        }

        private static string SanitizeTagValue(string value)
        {
            // Keep filename-safe inside brackets
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(value.Length);
            foreach (var ch in value)
                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            return sb.ToString().Trim();
        }

        private static string SanitizeForPath(string name)
        {
            if (string.IsNullOrEmpty(name)) return "Unknown";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
                sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
            return sb.ToString().Trim().TrimEnd('.');
        }

        private static int? ExtractYear(StremioMeta meta)
        {
            if (meta == null) return null;

            if (int.TryParse(meta.Year, out var y) && y is > 1800 and < 2200)
                return y;

            if (meta.Released is DateTime dt)
                return dt.Year;

            // "2007-2019", "2020-", or "2015"
            if (!string.IsNullOrWhiteSpace(meta.ReleaseInfo))
            {
                var s = meta.ReleaseInfo.Trim();

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
    }
}