using MediaBrowser.Model.Tasks;

namespace Gelato.ScheduledTasks;

public sealed class SyncMetaTask(GelatoManager manager) : IScheduledTask
{
    public string Name => "Sync metadata";
    public string Key => "SyncMeta";

    public string Description =>
        "Fetches missing season/episodes for continuing series and refreshes premiere dates for movies without one.";

    public string Category => "Gelato";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(24).Ticks,
            },
        ];
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await manager.SyncSeries(Guid.Empty, cancellationToken).ConfigureAwait(false);
        progress.Report(50);
        await manager.SyncMovieMeta(Guid.Empty, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}
