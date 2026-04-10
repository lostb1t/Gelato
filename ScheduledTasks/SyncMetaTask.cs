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
        var seriesProgress = new Progress<double>(p => progress.Report(p * 0.5));
        await manager
            .SyncSeries(Guid.Empty, cancellationToken, seriesProgress)
            .ConfigureAwait(false);

        var movieProgress = new Progress<double>(p => progress.Report(50 + p * 0.5));
        await manager
            .SyncMovieMeta(Guid.Empty, cancellationToken, movieProgress)
            .ConfigureAwait(false);

        progress.Report(100);
    }
}
