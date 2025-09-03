#pragma warning disable SA1611
#pragma warning disable SA1591
#pragma warning disable SA1615
#pragma warning disable CS0165

using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Entities.Movies;

namespace Jellyfin.Plugin.ExternalMedia;

[ApiController]
//[Authorize(Policy = "DefaultAuthorization")]
//[Authorize]
[Route("externalmedia")]
public class ExternalMediaApiController : ControllerBase
{
    private readonly ILogger<ExternalMediaApiController> _log;
    private readonly ILoggerFactory _loggerFactory;
    // private readonly IFileSystem _fileSystem;
    private readonly ExternalMediaStremioProvider _provider;
    private readonly IServerConfigurationManager _config;
    private readonly IUserManager _user;
    private readonly ILibraryManager _library;
    private readonly IItemRepository _repo;
    private readonly IDtoService _dtoService;
   // private readonly ExternalMediaRefresh _refresh;


    public ExternalMediaApiController(
        ILoggerFactory loggerFactory,
    //    ExternalMediaRefresh refresh,
        ExternalMediaStremioProvider provider,
        IDtoService dtoService,
        IServerConfigurationManager config,
        IUserManager userManager,
IItemRepository repo,
        ILibraryManager libraryManager)
    {
        _loggerFactory = loggerFactory;
     //   _refresh = refresh;
        _log = loggerFactory.CreateLogger<ExternalMediaApiController>();
        _provider = provider; _dtoService = dtoService;
        _config = config;
        _repo = repo;
        _user = userManager;
        _library = libraryManager;
    }

    // [HttpGet("image/{id}/{image_type}")]
    [HttpGet("test")]
    //[Authorize]
    // [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> Image(
        // [FromRoute] string id,
        // [FromRoute] ImageType image_type
        )
    {
        var found = _library.GetItemById(Guid.Parse("a3f08293726362d3531ab671f857d968"));
        using var cts = new CancellationTokenSource();
      //  await _refresh.RefreshAsync(found, cts.Token).ConfigureAwait(false);

        return NotFound();
    }
}
