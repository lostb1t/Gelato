using MediaBrowser.Model.Tasks;

namespace Gelato.ScheduledTasks;

public sealed class SyncMetaTask(GelatoManager manager) : IScheduledTask
{
    public string Name => "Sync metadata";
    public string Key => "SyncMeta";

    public string Description =>
        "Fetches missing seasons/episodes for continuing series. For movies, fetches digital release dates from TMDB and updates the release date used for filtering. Run this after enabling 'Filter unreleased items' to fix existing library items.";

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
