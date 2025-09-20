using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Controller.Dto;
using Jellyfin.Data.Enums;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Gelato.Common;

namespace Gelato.Filters;

public class InsertActionFilter : IAsyncResourceFilter, IOrderedFilter
{
    private readonly ILibraryManager _library;
    private readonly IItemRepository _repo;
    private readonly IMediaSourceManager _mediaSources;
    private readonly IDtoService _dtoService;
    private readonly GelatoStremioProvider _stremioProvider;
    private readonly ILogger<InsertActionFilter> _log;
    private readonly GelatoManager _manager;
    private readonly IMediaSourceManager _sourceManager;
    private readonly IProviderManager _provider;
    private readonly IFileSystem _fileSystem;
    private readonly ILibraryMonitor _libraryMonitor;

    public int Order { get; set; } = int.MinValue;

    public InsertActionFilter(
        ILibraryManager library,
        IFileSystem fileSystem,
        IItemRepository repo,
        IMediaSourceManager mediaSources,
        GelatoManager manager,
        IDtoService dtoService,
        GelatoStremioProvider stremioProvider,
        IProviderManager provider,
        ILibraryMonitor libraryMonitor,
        IMediaSourceManager sourceManager,
        ILogger<InsertActionFilter> log)
    {
        _library = library;
        _sourceManager = sourceManager;
        _repo = repo;
        _mediaSources = mediaSources;
        _dtoService = dtoService;
        _provider = provider;
        _stremioProvider = stremioProvider;
        _fileSystem = fileSystem;
        _manager = manager;
        _log = log;
        _libraryMonitor = libraryMonitor;
    }

    public async Task OnResourceExecutionAsync(ResourceExecutingContext ctx, ResourceExecutionDelegate next)
    {
      
     var fullUrl = ctx.HttpContext.Request.GetDisplayUrl();

       // _log.LogInformation("Requested URL: {Url}", fullUrl); 
        if (!IsItemsAction(ctx))
        {
            await next();
            return;
        }

        if (!_manager.TryGetRouteGuid(ctx, out var guid))
        {
            await next();
            return;
        }

        var stremioMeta = _manager.GetStremioMeta(guid);
        if (stremioMeta is null)
        {
            await next();
            return;
        }

        var found = _manager.FindByStremioId(stremioMeta.Id) as Video;
        if (found is not null)
        {
            _log.LogInformation("media already exist, do nothing");
            ReplaceGuid(ctx, found.Id);
            await next();
            return;
        }

        bool isSeries = stremioMeta.Type == StremioMediaType.Series;
        var root = isSeries ? _manager.TryGetSeriesFolder() : _manager.TryGetMovieFolder();
        if (root is null)
        {
            _log.LogWarning("no {Type} folder configured", isSeries ? "Series" : "Movie");
            await next();
            return;
        }

        var meta = await _stremioProvider.GetMetaAsync(stremioMeta.Id, stremioMeta.Type).ConfigureAwait(false);
        if (meta is null)
        {
            _log.LogWarning("stremio meta not found for {0} {1}", stremioMeta.Id, stremioMeta.Type);
            await next();
            return;
        }

        //using (new TimedBlock("Process data")) {
      
       BaseItem? baseItem = null;

try
{
    (baseItem, _) = await _manager.InsertMeta(
        root,
        meta,
        false,
        true,
        CancellationToken.None);
}
catch (Exception)
{

    // Fallback when the UI triggers this endpoint twice and causes a duplicate
    var tempItem = _stremioProvider.IntoBaseItem(meta);   
var timeout  = TimeSpan.FromSeconds(10);
var interval = TimeSpan.FromSeconds(1);
var start    = DateTime.UtcNow;

while (DateTime.UtcNow - start < timeout)
{

    baseItem = _manager.FindByProviderIds(tempItem.ProviderIds, tempItem.GetBaseItemKind());
    if (baseItem != null)
    {
        break;
    }

    await Task.Delay(interval);
}

 
  }

if (baseItem is not null)
{
    ReplaceGuid(ctx, baseItem.Id);
}
      
     // }
        await next();
    }

    public void StartSeriesRefreshDettached(Folder series)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using var ct = new CancellationTokenSource();
                var options = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                    ForceSave = true
                };
                await series.RefreshMetadata(options, CancellationToken.None);
                //ValidateChildren
                //await _seriesManager.CreateSeriesTreesAsync(root, meta, false, CancellationToken.None);
            }
            catch (Exception ex)
            {
                //_log.LogError(ex, "Gelato: background refresh failed for {Name}", root.Name);
            }
        });
    }

    private bool IsItemsAction(ResourceExecutingContext ctx)
    {
        if (ctx.ActionDescriptor is not ControllerActionDescriptor cad)
            return false;

        // _log.LogInformation("Gelato: Action = {Action}", cad.ActionName);
        return string.Equals(cad.ActionName, "GetItems", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cad.ActionName, "GetItem", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cad.ActionName, "GetItemLegacy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cad.ActionName, "GetItemsByUserIdLegacy", StringComparison.OrdinalIgnoreCase);

    }


    private void ReplaceGuid(ResourceExecutingContext ctx, Guid value)
    {
        // Replace route values
        var rd = ctx.RouteData.Values;
        foreach (var key in new[] { "id", "Id", "ID", "itemId", "ItemId", "ItemID" })
        {
            if (rd.TryGetValue(key, out var raw) && raw is not null)
            {
                _log.LogInformation("replacing route {Key} {Old} → {New}", key, raw, value);
                ctx.RouteData.Values[key] = value.ToString();
            }
        }

        // Replace query string "ids"
        // var request = ctx.HttpContext.Request;
        // var parsed = QueryHelpers.ParseQuery(request.QueryString.Value ?? "");

        // if (parsed.TryGetValue("ids", out var existing) && existing.Count == 1)
        // {
        //     _log.LogInformation("Gelato: Replacing query ids {Old} → {New}", existing[0], value);

        //     var dict = new Dictionary<string, StringValues>(parsed)
        //     {
        //         ["ids"] = new StringValues(value.ToString())
        //     };

        //     ctx.HttpContext.Request.QueryString = QueryString.Create(dict);
        // }

        // mutation for query is not allowed so we set it like this aswell.
        ctx.HttpContext.Items["GuidResolved"] = value;
    }

}