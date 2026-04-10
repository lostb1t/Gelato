using MediaBrowser.Model.Tasks;

namespace Gelato.ScheduledTasks;

public sealed class SyncReleaseDatesTask(GelatoManager manager) : IScheduledTask
{
    public string Name => "Sync release dates";
    public string Key => "SyncReleaseDates";

    public string Description =>
        "Sets the digital release date on all Gelato movies and series items. Used by the 'Filter unreleased items' feature. Run this after enabling that filter to fix existing library items.";

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
            .SyncReleaseDates(Guid.Empty, cancellationToken, progress)
            .ConfigureAwait(false);
        progress.Report(100);
    }
}
