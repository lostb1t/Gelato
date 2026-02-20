using MediaBrowser.Model.Tasks;

namespace Gelato.ScheduledTasks {
    public sealed class SyncRunningSeriesTask(GelatoManager manager) : IScheduledTask {
        public string Name => "Fetch missing season/episodes";
        public string Key => "SyncRunningSeries";

        public string Description =>
            "Scans all TV libraries for continuing series and builds their series trees.";

        public string Category => "Gelato";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() {
            return [
                new TaskTriggerInfo {
                    Type = TaskTriggerInfoType.IntervalTrigger,
                    IntervalTicks = TimeSpan.FromHours(24).Ticks,
                }
            ];
        }

        public async Task ExecuteAsync(IProgress<double> progress,
            CancellationToken cancellationToken) {
            await manager.SyncSeries(Guid.Empty, cancellationToken);
        }
    }
}
