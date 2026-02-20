using System.Collections.Concurrent;
using Gelato.Decorators;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.Tasks {
    public sealed class PurgeGelatoSyncTask : IScheduledTask {
        private readonly ILogger<PurgeGelatoSyncTask> _log;
        private readonly GelatoManager _manager;
        private readonly ILibraryManager _library;
        private readonly GelatoItemRepository _repo;

        public PurgeGelatoSyncTask(
            ILibraryManager libraryManager,
            ILogger<PurgeGelatoSyncTask> log,
                    GelatoItemRepository repo,
            GelatoManager manager
        ) {
            _log = log;
            _library = libraryManager;
            _manager = manager;
            _repo = repo;
        }

        public string Name => "WARNING: purge all gelato items";
        public string Key => "PurgeGelatoSyncTask";
        public string Description => "Removes all stremio items (local items are kept)";
        public string Category => "Gelato Maintenance";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct) {
            var stats = new ConcurrentDictionary<BaseItemKind, int>();

            var items = _library
                .GetItemList(new InternalItemsQuery {
                    IncludeItemTypes = new[]
                    {
                BaseItemKind.Movie,
                BaseItemKind.Series,
                BaseItemKind.BoxSet,
                    },
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
                .OfType<BaseItem>()
                .Where(i => _manager.IsGelato(i))
                .ToList();

            int totalItems = items.Count;
            int processedItems = 0;

            foreach (var item in items) {
                var kind = item.GetBaseItemKind();
                try {
                    _library.DeleteItem(
            item,
            new DeleteOptions { DeleteFileLocation = true },
            true);
                }
                catch (Exception ex) {
                    _log.LogWarning(ex, "Failed to delete item {ItemId}", item.Id);
                }

                stats.AddOrUpdate(kind, 1, (_, count) => count + 1);

                processedItems++;
                double currentProgress = (double)processedItems / totalItems * 100;
                progress?.Report(currentProgress);
            }

            _manager.ClearCache();
            progress?.Report(100.0);

            var parts = stats.Select(kv => $"{kv.Key}={kv.Value}");
            var line = string.Join(", ", parts);

            _log.LogInformation("Deleted: {Stats} (Total={Total})", line, stats.Values.Sum());
        }
    }
}
