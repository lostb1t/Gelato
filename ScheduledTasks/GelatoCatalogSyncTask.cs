using Gelato.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.ScheduledTasks;

public sealed class GelatoCatalogItemsSyncTask(
    ILogger<GelatoCatalogItemsSyncTask> log,
    CatalogImportService importService)
    : IScheduledTask {
    public string Name => "Import Gelato Catalogs";
    public string Key => "GelatoCatalogItemsSync";
    public string Description => "Imports items from enabled Stremio catalogs into Jellyfin.";
    public string Category => "Gelato";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct) {
        log.LogInformation("Starting Gelato catalog sync task...");
        await importService.SyncAllEnabledAsync(ct, progress).ConfigureAwait(false);
        log.LogInformation("Gelato catalog sync task finished.");
    }
}
