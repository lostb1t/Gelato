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
        "Resets downloaded gelato images so they are re-fetched on next access, and clears Jellyfin's processed image cache";
    public string Category => "Gelato Maintenance";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        // Zero out downloaded source images so ImageProcessorDecorator re-downloads
        // on next request. The .url sidecars are kept so the URL is still known.
        var gelatoImagesDir = Path.Combine(appPaths.DataPath, "gelato", "images");
        if (Directory.Exists(gelatoImagesDir))
        {
            foreach (
                var file in Directory.EnumerateFiles(
                    gelatoImagesDir,
                    "*.jpg",
                    SearchOption.AllDirectories
                )
            )
            {
                File.WriteAllBytes(file, Array.Empty<byte>());
            }
            log.LogInformation(
                "CleanGelatoImageCacheTask: reset source images in {Dir}",
                gelatoImagesDir
            );
        }

        progress?.Report(50.0);

        // Clear Jellyfin's processed/resized image cache
        var imageCacheDir = appPaths.ImageCachePath;
        if (Directory.Exists(imageCacheDir))
        {
            Directory.Delete(imageCacheDir, recursive: true);
            log.LogInformation(
                "CleanGelatoImageCacheTask: deleted image cache {Dir}",
                imageCacheDir
            );
        }

        progress?.Report(100.0);
        return Task.CompletedTask;
    }
}
