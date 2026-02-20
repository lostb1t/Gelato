using MediaBrowser.Model.Tasks;

namespace Gelato.Tasks {
    public sealed class SyncRunningSeriesTask : IScheduledTask {
        private readonly GelatoManager _manager;

        public SyncRunningSeriesTask(
            GelatoManager manager
        ) {
            _manager = manager;
        }

        public string Name => "Fetch missing season/episodes";
        public string Key => "SyncRunningSeries";
        public string Description =>
            "Scans all TV libraries for continuing series and builds their series trees.";
        public string Category => "Gelato";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.IntervalTrigger,
                    IntervalTicks = TimeSpan.FromHours(24).Ticks,
                },
            };
        }

        public async Task ExecuteAsync(
            IProgress<double> progress,
            CancellationToken cancellationToken
        ) {
            await _manager.SyncSeries(true, Guid.Empty, progress, cancellationToken);
        }
    }
}
