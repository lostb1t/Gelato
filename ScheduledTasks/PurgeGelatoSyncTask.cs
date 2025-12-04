using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelato;
using Gelato.Common;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.Tasks
{
    public sealed class PurgeGelatoSyncTask : IScheduledTask
    {
        private readonly ILogger<PurgeGelatoSyncTask> _log;
        private readonly GelatoManager _manager;
        private readonly ILibraryManager _library;

        public PurgeGelatoSyncTask(
            ILibraryManager libraryManager,
            ILogger<PurgeGelatoSyncTask> log,
            GelatoManager manager
        )
        {
            _log = log;
            _library = libraryManager;
            _manager = manager;
        }

        public string Name => "WARNING: purge all gelato items";
        public string Key => "PurgeGelatoSyncTask";
        public string Description => "Removes all stremio items (local items are kept)";
        public string Category => "Gelato Maintenance";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
        {
            var stats = new ConcurrentDictionary<BaseItemKind, int>();

            var parentQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[]
                {
                    BaseItemKind.Movie,
                    BaseItemKind.Series,
                    BaseItemKind.BoxSet,
                },
                Recursive = true,
                HasAnyProviderId = new()
                {
                    { "Stremio", string.Empty },
                    { "stremio", string.Empty },
                },
                GroupByPresentationUniqueKey = false,
                GroupBySeriesPresentationUniqueKey = false,
                CollapseBoxSetItems = false,
                IsDeadPerson = true,
            };

            var parentItems = _library
                .GetItemList(parentQuery)
                .OfType<BaseItem>()
                .Where(i => _manager.IsGelato(i))
                .ToList();

            await DeleteItemsAsync(parentItems, progress, stats, ct);

            var childQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Season, BaseItemKind.Episode },
                Recursive = true,
                HasAnyProviderId = new()
                {
                    { "Stremio", string.Empty },
                    { "stremio", string.Empty },
                },
                GroupByPresentationUniqueKey = false,
                GroupBySeriesPresentationUniqueKey = false,
                CollapseBoxSetItems = false,
                IsDeadPerson = true,
            };

            var childItems = _library
                .GetItemList(childQuery)
                .OfType<BaseItem>()
                .Where(i => _manager.IsGelato(i))
                .ToList();

            await DeleteItemsAsync(childItems, progress, stats, ct);

            _manager.ClearCache();
            progress?.Report(100.0);

            var ordered = stats.OrderBy(k => k.Key.ToString());
            var parts = ordered.Select(kv => $"{kv.Key}={kv.Value}");
            var line = string.Join(", ", parts);

            _log.LogInformation("Deleted: {Stats} (Total={Total})", line, stats.Values.Sum());
        }

        private async Task DeleteItemsAsync(
            IReadOnlyCollection<BaseItem> items,
            IProgress<double> progress,
            ConcurrentDictionary<BaseItemKind, int> stats,
            CancellationToken ct
        )
        {
            if (items.Count == 0)
                return;

            var deleted = 0;
            var total = items.Count;

            var opts = new ParallelOptions { MaxDegreeOfParallelism = 50, CancellationToken = ct };

            await Parallel.ForEachAsync(
                items,
                opts,
                async (item, ct2) =>
                {
                    ct2.ThrowIfCancellationRequested();

                    try
                    {
                        _library.DeleteItem(
                            item,
                            new DeleteOptions { DeleteFileLocation = false },
                            true
                        );

                        stats.AddOrUpdate(item.GetBaseItemKind(), 1, (_, old) => old + 1);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Failed to delete item {ItemId}", item.Id);
                    }

                    var d = Interlocked.Increment(ref deleted);
                    progress?.Report(Math.Min(100.0, (double)d / total * 100.0));

                    await Task.CompletedTask;
                }
            );
        }
    }
}
