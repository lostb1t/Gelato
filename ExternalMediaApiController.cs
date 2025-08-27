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


    public ExternalMediaApiController(
        ILoggerFactory loggerFactory,
        ExternalMediaStremioProvider provider,
        IDtoService dtoService,
        IServerConfigurationManager config,
        IUserManager userManager,
IItemRepository repo,
        ILibraryManager libraryManager)
    {
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<ExternalMediaApiController>();
        _provider = provider; _dtoService = dtoService;
        _config = config;
        _repo = repo;
        _user = userManager;
        _library = libraryManager;
    }

    [HttpGet("image/{id}/{image_type}")]
    //[Authorize]
    // [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<IActionResult> Image(
        [FromRoute] string id,
        [FromRoute] ImageType image_type)
    {

        // var meta = await _provider.GetMetaAsync(item, ct);

        // var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions {
        //     WriteIndented = true,
        //     DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        //     ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles // safe for graphs
        // });
        // _log.LogInformation("Object dump:\n{Json}", json);
        // var needRefresh = NeedsRefresh(dto);
        // if (!needRefresh)
        // {
        //     await next(); return;
        // }
        // var ct = context.HttpContext.RequestAborted;
        // _library.GetItemById(id);
        _log.LogInformation("Getting metadata for {Type}/{Id}", image_type, id);
        var guid = _library.GetNewItemId(id, typeof(Movie));
        var item = _library.GetItemById(guid);
        if (item is null)
        {
            _log.LogDebug("ExternalMedia: item {Id} not found in library", id);
            return NotFound();
        }

        var imdb = item.GetProviderId("Imdb");
        if (string.IsNullOrEmpty(imdb))
        {
            _log.LogDebug("ExternalMedia: no IMDb id for {Id}", id);
            return NotFound(); ;
        }

        var meta = await _provider.GetMetaAsync(imdb, "movie");
        if (meta is null)
        {
            _log.LogWarning("Not metadata found for {Type}/{Id}", id, "movie");
            return NotFound();
        }

        _log.LogInformation("ExternalMedia: applying meta for {Id}", id);
        _provider.ApplyMetaToEntity(item, meta);
        _repo.SaveItems(new[] { item }, CancellationToken.None);
        _repo.SaveImages(item);



        var image = item.GetImages(image_type).FirstOrDefault();

        // pick a random image url from a small list
        //     var urls = new[]
        //     {
        //     "https://picsum.photos/seed/1/600/900",
        //     "https://picsum.photos/seed/2/600/900",
        //     "https://picsum.photos/seed/3/600/900",
        //     "https://picsum.photos/seed/4/600/900"
        // };

        //     var rnd = new Random();
        //     var chosen = urls[rnd.Next(urls.Length)];

        //     // _logger.LogInformation("Redirecting image request for item {Id} to {Url}", id, chosen);

      if (image!.Path.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase))
    
        {

            return NotFound();
        }
        
        return Redirect(image!.Path); // 302 redirect
    }
}
