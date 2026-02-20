using Gelato.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.ScheduledTasks;

public sealed class GelatoCatalogItemsSyncTask : IScheduledTask {
    private readonly ILogger<GelatoCatalogItemsSyncTask> _log;
    private readonly CatalogImportService _importService;

    public GelatoCatalogItemsSyncTask(
        ILogger<GelatoCatalogItemsSyncTask> log,
        CatalogImportService importService
    ) {
        _log = log;
        _importService = importService;
    }

    public string Name => "Import Gelato Catalogs";
    public string Key => "GelatoCatalogItemsSync";
    public string Description => "Imports items from enabled Stremio catalogs into Jellyfin.";
    public string Category => "Gelato";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct) {
        _log.LogInformation("Starting Gelato catalog sync task...");
        await _importService.SyncAllEnabledAsync(ct, progress).ConfigureAwait(false);
        _log.LogInformation("Gelato catalog sync task finished.");
    }
}
