#pragma warning disable SA1611
#pragma warning disable SA1591
#pragma warning disable SA1615
#pragma warning disable CS0165

using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Persistence;
// using Jellyfin.Api.Controllers;
using Gelato.Controllers;
using Microsoft.AspNetCore.Http;
// using Jellyfin.Api.Extensions;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;


namespace Gelato;

[ApiController]
//[Authorize(Policy = "DefaultAuthorization")]
//[Authorize]
[Route("externalmedia")]
public class GelatoApiController : ControllerBase
{
    private readonly ILogger<GelatoApiController> _log;
    private readonly ILoggerFactory _loggerFactory;
    // private readonly IFileSystem _fileSystem;
    // private readonly GelatoStremioProvider _provider;
    private readonly IServerConfigurationManager _config;
    private readonly IUserManager _user;
    private readonly ILibraryManager _library;
    private readonly IItemRepository _repo;
    private readonly IDtoService _dtoService;
    //private readonly ExternalMediaManager _manager;
    // private readonly GelatoRefresh _refresh;


    public GelatoApiController(
        ILoggerFactory loggerFactory,
        IDtoService dtoService,
        IServerConfigurationManager config,
        IUserManager userManager,
    //    ExternalMediaManager manager,
        IItemRepository repo,
        ILibraryManager libraryManager)
    {
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<GelatoApiController>();
        // _provider = provider; _dtoService = dtoService;
        _config = config;
        _repo = repo;
        _user = userManager;
        _library = libraryManager;
        // _manager = manager;
    }

    // /// <summary>
    // /// Deletes an item from the library and filesystem.
    // /// </summary>
    // /// <param name="itemId">The item id.</param>
    // /// <response code="204">Item deleted.</response>
    // /// <response code="401">Unauthorized access.</response>
    // /// <response code="404">Item not found.</response>
    // /// <returns>A <see cref="NoContentResult"/>.</returns>
    // [EndpointOverride<LibraryController>(nameof(LibraryController.DeleteItem))]
    // [ProducesResponseType(StatusCodes.Status204NoContent)]
    // [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    // [ProducesResponseType(StatusCodes.Status404NotFound)]
    // public public async Task<ActionResult<MediaBrowser.Controller.DeleteItem>>  DeleteItem(Guid itemId)
    // {
    //     var userId = User.GetUserId();
    //     var isApiKey = User.GetIsApiKey();
    //     var user = userId.IsEmpty() && isApiKey
    //         ? null
    //         : _user.GetUserById(userId);

    //     if (user is null && !isApiKey)
    //     {
    //         return NotFound();
    //     }

    //     var item = _library.GetItemById<BaseItem>(itemId, user);
    //     if (item is null)
    //     {
    //         return base.DeleteItem(itemId);
    //     }
    // }

    // [HttpGet("image/{id}/{image_type}")]
    [HttpPost("test")]
    // [Authorize]
    // [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> Image(
        // [FromRoute] string id,
        // [FromRoute] ImageType image_type
        )
    {

        return NotFound();


    }
}
