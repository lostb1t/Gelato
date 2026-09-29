#pragma warning disable SA1611, SA1591, SA1615, CS0165

using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Gelato.Controllers;

[ApiController]
[Route("gelato")]
public sealed class GelatoApiController : ControllerBase
{
    private readonly GelatoManager _gelatoManager;

    public GelatoApiController(GelatoManager gelatoManager)
    {
        _gelatoManager = gelatoManager;
    }

    [HttpGet("meta/{stremioMetaType}/{Id}")]
    [Authorize]
    public async Task<ActionResult<StremioMeta>> GelatoMeta(
        [FromRoute, Required] StremioMediaType stremioMetaType,
        [FromRoute, Required] string id
    )
    {
        var cfg = GelatoPlugin.Instance!.GetConfig(Guid.Empty);
        var meta = await cfg.Stremio.GetMetaAsync(id, stremioMetaType);
        if (meta is null)
        {
            return NotFound();
        }
        return meta;
    }

    // [HttpGet("catalogs")]
    // Moved to CatalogController

    [HttpGet("subtitles/{itemId:guid}")]
    public ActionResult<IEnumerable<StremioSubtitle>> GetSubtitles(
        [FromRoute, Required] Guid itemId
    )
    {
        var subs = _gelatoManager.GetStremioSubtitlesCache(itemId);
        return Ok(subs ?? new List<StremioSubtitle>());
    }
}
