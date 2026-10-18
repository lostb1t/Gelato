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
    public ActionResult<object> GetLibraries([FromQuery] string? basePath)
    {
        try
        {
            return Ok(
                new
                {
                    Libraries = folders.GetLibraries(basePath),
                    DefaultBasePath = folders.GetDefaultBasePath(),
                }
            );
        }
        catch (IOException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// A folder for a library the user is about to create in Jellyfin, which takes none without.
    /// </summary>
    [HttpPost("folder")]
    public ActionResult<object> PrepareFolder([FromQuery] string? name, [FromQuery] string? basePath)
    {
        try
        {
            return Ok(new { Path = folders.PrepareFolder(name, basePath) });
        }
        catch (Exception ex)
            when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not prepare a Gelato folder for {Name}", name);
            return BadRequest(ex.Message);
        }
    }

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
