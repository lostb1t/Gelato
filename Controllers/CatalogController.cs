using Gelato.Configuration;
using Gelato.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Library;
using System.ComponentModel.DataAnnotations;
using static Gelato.Configuration.PluginConfiguration;

namespace Gelato.Controllers;

[ApiController]
[Route("gelato/catalogs")]
[Authorize]
public class CatalogController : ControllerBase {
    private readonly ILogger<CatalogController> _logger;
    private readonly CatalogService _catalogService;
    private readonly CatalogImportService _importService;
    private readonly ILibraryManager _libraryManager;
    public CatalogController(
        ILogger<CatalogController> logger,
        CatalogService catalogService,
        CatalogImportService importService,
                  ILibraryManager libraryManager
    ) {
        _logger = logger;
        _catalogService = catalogService;
        _importService = importService;
                _libraryManager = libraryManager;
    }

    [HttpGet]
    public async Task<ActionResult<List<CatalogConfig>>> GetCatalogs() {
        // Use Global user for now, or HttpContext.User if we want per-user catalogs later
        // But CatalogService currently uses Guid.Empty for global config if passed
        // We'll stick to global administration for now as per plan
        return await _catalogService.GetCatalogsAsync(Guid.Empty);
    }

    [HttpPost("{id}/{type}/config")]
    public ActionResult UpdateConfig(
        [FromRoute] string id,
        [FromRoute] string type,
        [FromBody] CatalogConfig config
    ) {
        if (config.Id != id || config.Type != type) {
            return BadRequest("ID/Type mismatch");
        }
        
        _catalogService.UpdateCatalogConfig(config);
        return Ok();
    }

    [HttpPost("{id}/{type}/import")]
    public async Task<ActionResult> TriggerImport(
        [FromRoute] string id,
        [FromRoute] string type
    ) {
        _logger.LogInformation("Manual import triggered for {Id} {Type}", id, type);
        
        // Run in background? Or await?
        // User probably wants to know it started. 
        // Awaiting might timeout if it takes long.
        // But existing implementations awaited.
        // Let's fire and forget but log, or return accepted.
        // "Straight approach" -> maybe just await it so user sees errors? 
        // But browser timeout is 2 mins usually. Import can take longer.
        // Better to run in background.
        
        _ = Task.Run(async () => {
             try {
                 await _importService.ImportCatalogAsync(id, type, Guid.Empty, CancellationToken.None);
await _libraryManager.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);
             } catch (Exception ex) {
                 _logger.LogError(ex, "Error in manual import for {Id}", id);
             }
        });

        return Accepted();
    }
    [HttpPost("import-all")]
    public ActionResult ImportAll() {
        _logger.LogInformation("Manual import triggered for all enabled catalogs");
        
        _ = Task.Run(async () => {
             try {
                 await _importService.SyncAllEnabledAsync(CancellationToken.None);
             } catch (Exception ex) {
                 _logger.LogError(ex, "Error in manual import for all enabled catalogs");
             }
        });

        return Accepted();
    }
}
