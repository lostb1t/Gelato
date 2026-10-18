using MediaBrowser.Model.Tasks;

namespace Gelato.ScheduledTasks;

public sealed class SyncSeriesTreesTask(GelatoManager manager) : IScheduledTask
{
    public string Name => "Sync series trees";
    public string Key => "SyncSeriesTrees";

    public string Description =>
        "Fetches missing seasons and episodes for all continuing series. When 'Extend local series trees' is enabled, also fills in virtual items for locally scanned shows so you can stream episodes you don't have on disk.";

    public string Category => "Gelato";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks,
            },
        ];
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await manager
            .SyncSeriesTrees(Guid.Empty, cancellationToken, progress)
            .ConfigureAwait(false);
        progress.Report(100);
    }
}
