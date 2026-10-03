using Gelato.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Gelato.Controllers;

/// <summary>The libraries the settings page picks from, and Gelato's folder in each.</summary>
[ApiController]
[Route("gelato/libraries")]
[Authorize(Policy = Policies.RequiresElevation)]
public class LibraryController(ILogger<LibraryController> logger, LibraryFolderService folders)
    : ControllerBase
{
    [HttpGet]
    public ActionResult<object> GetLibraries([FromQuery] string? basePath) =>
        Ok(
            new
            {
                Libraries = folders.GetLibraries(basePath),
                DefaultBasePath = folders.GetDefaultBasePath(),
            }
        );

    /// <summary>
    /// Gelato's folder in the library, created and added to it when there is none yet.
    /// </summary>
    [HttpPost("{id}/folder")]
    public ActionResult<object> EnsureFolder([FromRoute] string id, [FromQuery] string? basePath)
    {
        try
        {
            return Ok(new { Path = folders.EnsureFolder(id, basePath) });
        }
        catch (ArgumentException ex)
        {
            return NotFound(ex.Message);
        }
        catch (Exception ex)
            when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogWarning(ex, "Could not create a Gelato folder for library {Id}", id);
            return BadRequest(ex.Message);
        }
    }
}
