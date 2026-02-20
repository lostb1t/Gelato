using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.ScheduledTasks {
    public sealed class PurgeGelatoStreamsTask : IScheduledTask {
        private readonly ILogger<PurgeGelatoStreamsTask> _log;

        private readonly GelatoManager _manager;
        private readonly ILibraryManager _library;

        public PurgeGelatoStreamsTask(
            ILibraryManager libraryManager,
            ILogger<PurgeGelatoStreamsTask> log,
            GelatoManager manager
        ) {
            _log = log;
            _library = libraryManager;
            _manager = manager;
        }

        public string Name => "Purge streams";
        public string Key => "PurgeGelatoStreamsTask";
        public string Description => "Removes all stremio streams";
        public string Category => "Gelato Maintenance";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() {
            return
            [
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.IntervalTrigger,
                    IntervalTicks = TimeSpan.FromDays(7).Ticks,
                }
            ];
        }

        public Task ExecuteAsync(
            IProgress<double> progress,
            CancellationToken cancellationToken
        ) {
            _log.LogInformation("purging streams");

            var query = new InternalItemsQuery {
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                Recursive = true,
                HasAnyProviderId = new Dictionary<string, string>
                {
                    { "Stremio", string.Empty },
                    { "stremio", string.Empty },
                },
                IsDeadPerson = true
            };

            var streams = _library
                .GetItemList(query)
                .OfType<Video>()
                .Where(v => _manager.IsStream(v))
                .ToArray();

            var total = streams.Length;

            var done = 0;

            foreach (var item in streams) {
                cancellationToken.ThrowIfCancellationRequested();

                try {
                    _library.DeleteItem(
            item,
            new DeleteOptions { DeleteFileLocation = true },
            true);
                }
                catch (Exception ex) {
                    _log.LogWarning(ex, "Failed to delete item {ItemId}", item.Id);
                }

                done++;
                var pct = Math.Min(100.0, ((double)done / total) * 100.0);
                progress?.Report(pct);
            }

            progress?.Report(100.0);
            _manager.ClearCache();

            _log.LogInformation("stream purge completed");
            return Task.CompletedTask;
        }
    }
}
