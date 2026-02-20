using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.ScheduledTasks {
    public sealed class PurgeGelatoSyncTask(
        ILibraryManager libraryManager,
        ILogger<PurgeGelatoSyncTask> log,
        GelatoManager manager)
        : IScheduledTask {
        public string Name => "WARNING: purge all gelato items";
        public string Key => "PurgeGelatoSyncTask";
        public string Description => "Removes all stremio items (local items are kept)";
        public string Category => "Gelato Maintenance";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken ct) {
            var stats = new ConcurrentDictionary<BaseItemKind, int>();

            var items = libraryManager
                .GetItemList(new InternalItemsQuery {
                    IncludeItemTypes =
                    [
                        BaseItemKind.Movie,
                        BaseItemKind.Series,
                        BaseItemKind.BoxSet
                    ],
                    Recursive = true,
                    HasAnyProviderId = new Dictionary<string, string>
                    {
                { "Stremio", string.Empty },
                { "stremio", string.Empty },
                    },
                    GroupByPresentationUniqueKey = false,
                    GroupBySeriesPresentationUniqueKey = false,
                    CollapseBoxSetItems = false,
                    IsDeadPerson = true,
                })
                .Where(manager.IsGelato)
                .ToList();

            var totalItems = items.Count;
            var processedItems = 0;

            foreach (var item in items) {
                var kind = item.GetBaseItemKind();
                try {
                    libraryManager.DeleteItem(
                        item,
                        new DeleteOptions { DeleteFileLocation = true },
                        true);
                }
                catch (Exception ex) {
                    log.LogWarning(ex, "Failed to delete item {ItemId}", item.Id);
                }

                stats.AddOrUpdate(kind, 1, (_, count) => count + 1);

                processedItems++;
                var currentProgress = (double)processedItems / totalItems * 100;
                progress?.Report(currentProgress);
            }

            manager.ClearCache();
            progress?.Report(100.0);

            var parts = stats.Select(kv => $"{kv.Key}={kv.Value}");
            var line = string.Join(", ", parts);

            log.LogInformation("Deleted: {Stats} (Total={Total})", line, stats.Values.Sum());
            return Task.CompletedTask;
        }
    }
}
