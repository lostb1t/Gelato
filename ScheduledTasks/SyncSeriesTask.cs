using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelato;
using Gelato.Common;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.Tasks
{
    public sealed class SyncRunningSeriesTask : IScheduledTask
    {
        private readonly ILibraryManager _library;
        private readonly ILogger<SyncRunningSeriesTask> _log;
        private readonly IFileSystem _fs;
        private readonly GelatoManager _manager;
        private readonly GelatoStremioProvider _stremio;

        public SyncRunningSeriesTask(
            ILibraryManager library,
            ILogger<SyncRunningSeriesTask> log,
            IFileSystem fs,
                          GelatoStremioProvider stremio,
            GelatoManager manager)
        {
            _library = library;
            _log = log;
            _fs = fs;
            _manager = manager;
            _stremio = stremio;
        }

        public string Name => "Gelato: Sync running series";
        public string Key => "SyncRunningSeries";
        public string Description => "Scans all TV libraries for continuing series and builds their series trees.";
        public string Category => "Gelato";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
                    {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks
            }
        };
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {

            var seriesFolder = _manager.TryGetSeriesFolder();
            var seriesItems = _library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series },
                SeriesStatuses = new[] { SeriesStatus.Continuing }
            }).OfType<Series>()
              .ToList();

            _log.LogInformation("found {Count} continuing series under TV libraries.", seriesItems.Count);

            var processed = 0;
            foreach (var series in seriesItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    _log.LogDebug("SyncRunningSeries: syncing series trees for {Name} ({Id})", series.Name, series.Id);

                    // var imdbId = series.GetProviderId("Imdb");
                    // if (imdbId is null)
                    // {
                    //     _log.LogWarning("SyncRunningSeries: skipping {Name} ({Id}) - no IMDB id", series.Name, series.Id);
                    //     continue;
                    // }

                    var meta = await _stremio.GetMetaAsync(series).ConfigureAwait(false);
                    if (meta is null)
                    {
                        _log.LogWarning("SyncRunningSeries: skipping {Name} ({Id}) - no metadata found", series.Name, series.Id);
                        continue;
                    }
                    await _manager.SyncSeriesTreesAsync(seriesFolder, meta, cancellationToken);
                    processed++;
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "SyncRunningSeries: failed for {Name} ({Id})", series.Name, series.Id);
                }
            }

            _log.LogInformation("SyncRunningSeries completed. Processed {Processed}/{Total} series.", processed, seriesItems.Count);
        }


    }
}
