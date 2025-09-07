using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Gelato.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Library;

namespace Gelato
{
    public partial class GelatoSeriesManager
    {
        private readonly IFileSystem _fs;
        private readonly ILogger<GelatoSeriesManager> _log;
        private readonly GelatoManager _manager;
        private readonly GelatoStremioProvider _stremioProvider;
        private readonly IItemRepository _repo;
        private readonly ILibraryManager _library;
        public GelatoSeriesManager(IFileSystem fs, ILibraryManager library, ILogger<GelatoSeriesManager> log, GelatoManager manager, GelatoStremioProvider stremioProvider, IItemRepository repo)
        {
            _fs = fs;
            _log = log;
            _library = library;
            _manager = manager;
            _stremioProvider = stremioProvider;
            _repo = repo;
        }

        public async Task<BaseItem?> CreateSeriesTreesAsync(
            Folder seriesRootFolder,
            StremioMeta seriesMeta,
            bool placeholders,
            CancellationToken ct)
        {
            if (seriesRootFolder is null || string.IsNullOrWhiteSpace(seriesRootFolder.Path))
                return null;

            var providerIds = seriesMeta.GetProviderIds();
            if (providerIds is null || providerIds.Count == 0)
                return null;

            _log.LogInformation("Gelato: series");
            //var seriesFolderName = GetSeriesFolderName(seriesMeta);
            //var seriesPath = Path.Combine(seriesRootFolder.Path, seriesFolderName);
            //Directory.CreateDirectory(seriesPath);

            var groups = (seriesMeta.Videos ?? Enumerable.Empty<StremioMeta>())
                .OrderBy(e => e.Season)
                .ThenBy(e => e.Episode)
                .GroupBy(e => e.Season)
                .ToList();
            if (groups.Count == 0)
                return null;

            //var seriesStremioUri = StremioUri.LoadFromString(stremioKey);
            var seriesItem = _library.GetItemList(new InternalItemsQuery
                {
                    ParentId = seriesRootFolder.Id,
                    HasAnyProviderId = _manager.GetProviderIds(seriesMeta),
                    Recursive = false
                })
                .OfType<Series>()
                .FirstOrDefault();
           // var seriesItem = _library.FindByPath(seriesPath, true) as Series;
            if (seriesItem is null)
            {
                seriesItem = (Series)_stremioProvider.IntoBaseItem(seriesMeta);
                if (seriesItem.Id == Guid.Empty) seriesItem.Id = Guid.NewGuid();
               // seriesItem.Path =  "stremio://{meta.Type}/{Id}";;
                seriesItem.PresentationUniqueKey = seriesItem.CreatePresentationUniqueKey();
                seriesRootFolder.AddChild(seriesItem);
            }

            foreach (var seasonGroup in groups)
            {
                ct.ThrowIfCancellationRequested();
                _log.LogInformation($"Gelato: season {seasonGroup.Key}");
                var seasonIndex = seasonGroup.Key;
                var seasonPath = $"{seriesItem.Path}:{seasonIndex}";
               // Directory.CreateDirectory(seasonPath);

                var seasonItem = _library.FindByPath(seasonPath, true) as Season;
                if (seasonItem is null)
                {
                    seasonItem = new Season
                    {
                        Id = Guid.NewGuid(),
                        Name = $"Season {seasonIndex:D2}",
                        IndexNumber = seasonIndex,
                        ParentId = seriesItem.Id,
                        SeriesId = seriesItem.Id,
                        SeriesName = seriesItem.Name,
                        Path = seasonPath,
                        SeriesPresentationUniqueKey = seriesItem.GetPresentationUniqueKey()
                    };
                    seasonItem.SetProviderId("stremio", seasonItem.Path);
                    seriesItem.AddChild(seasonItem);
                }

                // var existing = seasonItem
                //     .GetChildren(null, false)
                //     .OfType<Episode>()
                //     .ToDictionary(e => e.Id, e => e);
                var existing = _library.GetItemList(new InternalItemsQuery
                {
                    ParentId = seasonItem.Id,
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    Recursive = false
                })
                .OfType<Episode>()
                .ToDictionary(e => e.Path, e => e);

                var desiredIds = new HashSet<Guid>();

                foreach (var epMeta in seasonGroup)
                {
                    ct.ThrowIfCancellationRequested();
                   // if (placeholders)
                   // {
                   //     continue;
                   // }
                  //  var episodes = new List<Video>();
                    //var epMeta = await _stremioProvider.GetNetaAsync(epM.Id, StremioMediaType.Series);
                    //var streams = await _stremioProvider.GetStreamsAsync(epMeta.Id, StremioMediaType.Series);
                   // if (streams is null) continue;

                    //foreach (StremioStream stream in streams)
                    //{
                        ct.ThrowIfCancellationRequested();

                       // var epId = stream.GetGuid();
                        //desiredIds.Add(epId);

                       // string fileNameBase = $"{SanitizeForPath(seriesFolderName)} S{epMeta.Season:D2}E{epMeta.Episode:D2}";
                        //string? suffix = TryGetStreamSuffix(stream);
                        //string epFileName = epId.ToString();
                        //string epPath = Path.Combine(seasonPath, epFileName + ".strm");
                        var epPath = $"{seasonItem.Path}:{epMeta.Number}";
                        //   if (!placeholders)
                        //  {
                        //     await _manager.WriteAllTextAsync(epPath, stream.Url ?? string.Empty, ct).ConfigureAwait(false);
                        // }
                        var epItem = existing.GetValueOrDefault(epPath) ?? new Episode
                        {
                            Id = Guid.NewGuid(),
                            Name = epMeta.Name,
                            IndexNumber = epMeta.Number,
                            ParentIndexNumber = epMeta.Season,
                            ParentId = seasonItem.Id,
                            SeasonId = seasonItem.Id,
                            SeriesId = seriesItem.Id,
                            SeriesName = seriesItem.Name,
                            SeasonName = seasonItem.Name,
                            Path = epPath,
                            SeriesPresentationUniqueKey = seasonItem.SeriesPresentationUniqueKey,

                        };
                        epItem.SetProviderId("stremio", epItem.Path);

                        //epItem.Name = epMeta.Name;
                        //epItem.IsVirtualItem = placeholders;

                        //epItem.ShortcutPath = stream.Url;
                       // epItem.IsShortcut = true;
                        //epItem.Path = placeholders ? null : epPath;
                        //await _manager.SaveStrmAsync(
                        //    epItem.Path,
                        //    stream.Url
                        //);
                        _repo.SaveItems(new BaseItem[] { epItem }, ct);
                      //  episodes.Add(epItem);
                   // }

                    //await _manager.MergeVersions(episodes.ToArray());
                }


                

            }


            _log.LogInformation($"Gelato: done sync series");
            return seriesItem;
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