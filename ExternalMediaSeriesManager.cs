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
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Persistence;

namespace Jellyfin.Plugin.ExternalMedia
{
    public partial class ExternalMediaSeriesManager
    {
        private readonly IFileSystem _fs;
        private readonly ILogger<ExternalMediaSeriesManager> _log;
        private readonly ExternalMediaManager _manager;
        private readonly ExternalMediaStremioProvider _stremioProvider;
        private readonly IItemRepository _repo;

        public ExternalMediaSeriesManager(IFileSystem fs, ILogger<ExternalMediaSeriesManager> log, ExternalMediaManager manager, ExternalMediaStremioProvider stremioProvider, IItemRepository repo)
        {
            _fs = fs;
            _log = log;
            _manager = manager;
            _stremioProvider = stremioProvider;
            _repo = repo;
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
            bool placeholders,
            CancellationToken ct)
        {
            if (seriesRootFolder is null || string.IsNullOrWhiteSpace(seriesRootFolder.Path))
            {
                _log.LogWarning("SaveSeriesPlaceholdersAsync: invalid series root folder");
                return false;
            }

    //         _log.LogInformation("CreateSeriesTreesAsync: processing series meta {Id} '{Title}'", seriesMeta.Id, seriesMeta.Name);
    //         var _episodesBySeason = (seriesMeta.Videos ?? Enumerable.Empty<StremioMeta>())
    // .OrderBy(e => e.Season)
    // .ThenBy(e => e.Episode)
    // .GroupBy(e => e.Season)
    // .ToList();

    //         foreach (var group in _episodesBySeason)
    //         {
    //             _log.LogInformation("Season {Season} has {Count} episodes", group.Key, group.Count());

    //         }
    //         return true;

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

            // var episodes = (seriesMeta.Videos ?? Enumerable.Empty<StremioMeta>())
            //     .OrderBy(e => e.Season)
            //     .ThenBy(e => e.Episode)
            //     .ToList();
            var episodesBySeason = (seriesMeta.Videos ?? Enumerable.Empty<StremioMeta>())
                .OrderBy(e => e.Season)
                .ThenBy(e => e.Episode)
                .GroupBy(e => e.Season)
                .ToList();

            if (episodesBySeason.Count == 0)
            {
                _log.LogWarning("SaveSeriesPlaceholdersAsync: series has no episodes in meta; aborting");
                return false;
            }

            var serieItem = (Series)_stremioProvider.IntoBaseItem(seriesMeta);
            if (serieItem is null || serieItem.ProviderIds is null || serieItem.ProviderIds.Count == 0)
            {
                _log.LogWarning("ExternalMedia: Missing provider ids, skipping");
                return false;
            }

            serieItem.Path = seriesPath;
            serieItem.PresentationUniqueKey = serieItem.CreatePresentationUniqueKey();
            // save
            seriesRootFolder.AddChild(serieItem);

            int created = 0;

            foreach (var seasonGroup in episodesBySeason)

            {

                ct.ThrowIfCancellationRequested();

                var seasonPath = Path.Combine(seriesPath, $"Season {seasonGroup.Key:D2}");
                _log.LogInformation("SaveSeriesPlaceholdersAsync: creating season folder {SeasonPath}", seasonPath);
                if (Directory.Exists(seasonPath))
                {
                    _log.LogWarning("SaveSeriesPlaceholdersAsync: season folder already exists; skipping");
                    continue;
                }

                // var seasonItem = (Season)_stremioProvider.IntoBaseItem(seriesMeta);
                var seasonItem = new Season
                {
                    // Id = Guid.NewGuid().ToString(),
                    Name = $"Season {seasonGroup.Key:D2}",
                    IndexNumber = seasonGroup.Key,
                    ParentId = serieItem.Id,
                    SeriesId = serieItem.Id,
                    SeriesName = serieItem.Name,
                    Path = seasonPath,
                    SeriesPresentationUniqueKey = serieItem.GetPresentationUniqueKey()
                };

                Directory.CreateDirectory(seasonPath);
                serieItem.AddChild(seasonItem);

                var episodes = new List<Episode>();
                foreach (var ep in seasonGroup)
                {
                    ct.ThrowIfCancellationRequested();
                    //_log.LogInformation("SaveSeriesPlaceholdersAsync: creating episode {SxE} - {Title}", $"S{ep.Season:D2}E{ep.Episode:D2}", ep.Name ?? "(no title)");

                    var baseName = $"{SanitizeForPath(seriesFolderName)} S{ep.Season:D2}E{ep.Episode:D2}";
                    var epPath = Path.Combine(seasonPath, baseName + ".strm");

                    // var epItem = (Episode)_stremioProvider.IntoBaseItem(ep);
                    var epItem = new Episode
                    {
                        Name = ep.Name,
                        IndexNumber = ep.Number,
                        ParentIndexNumber = ep.Season,
                        ParentId = seasonItem.Id,
                        IsVirtualItem = true,
                        SeasonId = seasonItem.Id,
                        SeriesId = serieItem.Id,
                        // Overview = episode.Overview,
                        SeriesName = serieItem.Name,
                        SeriesPresentationUniqueKey = seasonItem.SeriesPresentationUniqueKey,
                        SeasonName = seasonItem.Name,
                        // DateLastSaved = DateTime.UtcNow
                    };

                     seasonItem.AddChild(epItem);
                    // seasonItem.SetParent(epItem);

                    //episodes.Add(epItem);

                    // Placeholder only; resolved later at playback by your resolver
                    //var placeholderUrl = BuildEpisodePlaceholderUrl(seriesMeta, ep);

                    //await _manager.WriteAllTextAsync(epPath, "https://test-videos.co.uk/vids/bigbuckbunny/mp4/h264/1080/Big_Buck_Bunny_1080_10s_30MB.mp4", ct).ConfigureAwait(false);
                    //created++;
                }
                //_repo.SaveItems(episodes, ct);
            }

            // _log.LogInformation(
            //     "SaveSeriesPlaceholdersAsync: created {Count} placeholder .strm files for '{Title}' at {SeriesPath}",
            //     created, title, seriesPath);
            return true;
            //return created > 0;
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