using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.ScheduledTasks;

public sealed class CleanGelatoImageCacheTask(
    IApplicationPaths appPaths,
    ILogger<CleanGelatoImageCacheTask> log
) : IScheduledTask
{
    public string Name => "Clean gelato image cache";
    public string Key => "CleanGelatoImageCacheTask";
    public string Description =>
        "Deletes Jellyfin's processed image cache so scaled/transcoded versions are regenerated on next request";
    public string Category => "Gelato Maintenance";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        var dir = appPaths.ImageCachePath;
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
            log.LogInformation("CleanGelatoImageCacheTask: deleted {Dir}", dir);
        }
        else
        {
            log.LogInformation("CleanGelatoImageCacheTask: nothing to clean at {Dir}", dir);
        }

        progress?.Report(100.0);
        return Task.CompletedTask;
    }
}
