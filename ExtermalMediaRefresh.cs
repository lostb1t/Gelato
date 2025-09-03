using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
// using Jellyfin.Data.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Jellyfin.Data.Enums;

using Jellyfin.Plugin.ExternalMedia.Common;
using MediaBrowser.Controller.Dto; // adjust namespace if needed

namespace Jellyfin.Plugin.ExternalMedia
{
    public class ExternalMediaRefresh
    {
        private readonly ILibraryManager _library;
        private readonly ILogger<ExternalMediaRefresh> _log;
        private readonly ExternalMediaStremioProvider _provider;
        private readonly ExternalMediaManager _manager;
        private readonly ExternalMediaSeriesManager _seriesManager;

        public ExternalMediaRefresh(
            ILibraryManager library,
            ExternalMediaSeriesManager seriesManager,
            ILogger<ExternalMediaRefresh> log,
            ExternalMediaStremioProvider provider,
            ExternalMediaManager manager)
        {
            _library = library;
            _seriesManager = seriesManager;
            _log = log;
            _provider = provider;
            _manager = manager;
        }

        public async Task RefreshAsync(BaseItem item, CancellationToken ct)
        {
            switch (item)
            {
                case Movie movie:
                    await RefreshMovieAsync(movie, ct).ConfigureAwait(false);
                    break;

                case Series series:
                    await RefreshSeriesAsync(series, ct).ConfigureAwait(false);
                    break;

                case Folder folder:
                    await RefreshFolderAsync(folder, ct).ConfigureAwait(false);
                    break;

                default:
                    _log.LogDebug("ExternalMedia: skipping unsupported item {Name} ({Type})", item.Name, item.GetType().Name);
                    break;
            }

            _log.LogInformation("ExternalMedia: Done refreshing {Name}", item.Name);
        }

        private async Task RefreshFolderAsync(Folder folder, CancellationToken ct)
        {
            _log.LogInformation("ExternalMedia: refreshing folder {Name}", folder.Name);

            var children = _library.GetItemList(new InternalItemsQuery
            {
                ParentId = folder.Id,
                Recursive = false
            });

            foreach (var child in children)
            {
                ct.ThrowIfCancellationRequested();
                await RefreshAsync(child, ct).ConfigureAwait(false);
            }

            QueueParentRefresh(folder);
        }
        private async Task RefreshMovieAsync(Movie movie, CancellationToken ct)
        {
            _log.LogInformation("ExternalMedia: refreshing movie {Name}", movie.Name);

            var streams = await _provider.GetStreamsAsync(movie).ConfigureAwait(false);
            if (streams == null || streams.Count == 0)
            {
                _log.LogWarning("ExternalMedia: no streams found for movie {Name}", movie.Name);
                return;
            }

            ReplaceStrmFiles(movie.Path, streams);
            QueueParentRefresh(movie);
        }

        private async Task RefreshSeriesAsync(Series series, CancellationToken ct)
        {
            _log.LogInformation("ExternalMedia: refreshing series {Name}", series.Name);

            var imdbId = series.GetProviderId(MetadataProvider.Imdb);
            var seriesMeta = await _provider.GetMetaAsync(imdbId, StremioMediaType.Series).ConfigureAwait(false);
            await _seriesManager.CreateSeriesTreesAsync(series.GetParent() as Folder, seriesMeta, false, ct).ConfigureAwait(false);
            // var seriesFolderName = _seriesManager.GetSeriesFolderName(seriesMeta);

            var seasons = series.GetSeasons(null, new DtoOptions()).OfType<Season>();
            //  _log.LogInformation($"ExternalMedia: refreshing season {seasons.Count()}");

            foreach (var season in seasons) {
                _manager.DeleteStrmFiles(season.Path);
            }

            foreach (var episode in seriesMeta.Videos ?? Enumerable.Empty<StremioMeta>())
            {
                ct.ThrowIfCancellationRequested();
                // var episodeImdbId = episode.GetProviderId(MetadataProvider.Imdb);

                // _log.LogInformation($"{imdbId}:{episode.Season}:{episode.Number}");
                var streams = await _provider.GetStreamsAsync($"{imdbId}:{episode.Season}:{episode.Number}", StremioMediaType.Series).ConfigureAwait(false);
                if (streams == null || streams.Count == 0)
                {
                    _log.LogInformation("ExternalMedia: no streams for {Ep}", episode.Name);
                    continue;
                }

                // var baseName = $"{SanitizeForPath(title)} S{ep.Season:D2}E{ep.Episode:D2}";

                foreach (var stream in streams)
                {
                    var targetPath = $"{series.Path}/Season {episode.Season:D2}/{Path.GetFileName(series.Path)} S{episode.Season:D2}E{episode.Episode:D2} - {stream.GetName()}.strm";
                    await _manager.WriteAllTextAsync(targetPath, stream.Url, ct).ConfigureAwait(false);
                }
                // if (!string.IsNullOrWhiteSpace(targetPath) && !Directory.Exists(targetPath))
                // {
                //     // If it's a file path, use its directory
                //     var dir = Path.GetDirectoryName(targetPath);
                //     if (!string.IsNullOrWhiteSpace(dir))
                //         targetPath = dir;
                // }

                // ReplaceStrmFiles(targetPath, streams);
            }

            QueueParentRefresh(series);
        }

        private void ReplaceStrmFiles(string? itemPath, IReadOnlyList<StremioStream> streams)
        {
            _log.LogInformation("ExternalMedia: Replacing .strm files in {Path} with {Count} streams", itemPath, streams.Count);
            if (string.IsNullOrWhiteSpace(itemPath))
            {
                _log.LogWarning("ExternalMedia: item has no path, cannot write strm files");
                return;
            }

            // If a file is passed, write into its directory.
            var targetDir = Directory.Exists(itemPath)
                ? itemPath
                : Path.GetDirectoryName(itemPath);

            if (string.IsNullOrWhiteSpace(targetDir))
            {
                _log.LogWarning("ExternalMedia: no target directory for path {Path}", itemPath);
                return;
            }

            try
            {
                if (!Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                // Remove old .strm files so we truly "replace"
                foreach (var old in Directory.EnumerateFiles(targetDir, "*.strm", SearchOption.TopDirectoryOnly))
                {
                    try { File.Delete(old); }
                    catch (Exception ex) { _log.LogDebug(ex, "ExternalMedia: failed deleting old strm {File}", old); }
                }

                foreach (var stream in streams)
                {
                    if (string.IsNullOrWhiteSpace(stream?.Url)) continue;

                    var safeName = SafeFileName(stream.Title ?? "stream") + ".strm";
                    var fullPath = Path.Combine(targetDir, safeName);

                    File.WriteAllText(fullPath, stream.Url);
                    _log.LogDebug("ExternalMedia: wrote {File}", fullPath);
                }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "ExternalMedia: failed to replace strm files in {Path}", targetDir);
            }
        }

        private static string SafeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
            return string.IsNullOrWhiteSpace(cleaned) ? "stream" : cleaned.Trim();
        }

        private void QueueParentRefresh(BaseItem item)
        {
            // return;
            _manager.QueueParentRefresh(item);
        }
    }
}