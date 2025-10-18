using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Jellyfin.Data.Enums;
using Gelato.Common;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Gelato.Filters;

public class InsertActionFilter : IAsyncActionFilter, IOrderedFilter
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
    private readonly LinkGenerator _links;
    private readonly KeyLock _lock = new();

    public int Order { get; set; } = 1;

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
        ILogger<InsertActionFilter> log,
        LinkGenerator links)
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
        _links = links;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        //         var req = ctx.HttpContext.Request;
        // _log.LogInformation("GELATTOTOOOOO: Requested path = {Path}{Query}", req.Path, req.QueryString);
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

        // Already exists?
        var item = _stremioProvider.IntoBaseItem(stremioMeta);
        var q = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { item.GetBaseItemKind() },
            HasAnyProviderId = item.ProviderIds,
            Recursive = true,
        };
        if (_library.GetItemList(q).OfType<BaseItem>().FirstOrDefault() is BaseItem existing)
        {
            _log.LogInformation("Media already exists; redirecting to canonical id {Id}", existing.Id);
            ReplaceGuid(ctx, existing.Id);
            await next();
            return;
        }

        // Need to create/import it
        bool isSeries = stremioMeta.Type == StremioMediaType.Series;
        var root = isSeries ? _manager.TryGetSeriesFolder() : _manager.TryGetMovieFolder();
        if (root is null)
        {
            _log.LogWarning("No {Type} folder configured", isSeries ? "Series" : "Movie");
            await next();
            return;
        }

        var meta = await _stremioProvider.GetMetaAsync(stremioMeta.Id, stremioMeta.Type).ConfigureAwait(false);
        if (meta is null)
        {
            _log.LogWarning("Stremio meta not found for {Id} {Type}", stremioMeta.Id, stremioMeta.Type);
            await next();
            return;
        }

        // Note: We do NOT filter unreleased items here because the user has explicitly
        // selected this item from search results to add to their library.

       BaseItem? baseItem = null;

await _lock.RunQueuedAsync(guid, async ct =>
{
    meta.Guid = guid;

    (baseItem, _) = await _manager.InsertMeta(
        root,
        meta,
        false,
        true,
        ct).ConfigureAwait(false);
});

        if (baseItem is not null)
        {
            ReplaceGuid(ctx, baseItem.Id);

           // if (TryBuildRedirect(ctx, out var url))
           // {
             //  _log.LogInformation($"REDIRECT {url}.");
             //  ctx.Result = new LocalRedirectResult(url);
             //  return;
           // }
        }

        await next();
    }

    private static bool IsItemsAction(ActionExecutingContext ctx)
    {
        if (ctx.ActionDescriptor is not ControllerActionDescriptor cad)
            return false;


        return string.Equals(cad.ActionName, "GetItems", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cad.ActionName, "GetItem", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cad.ActionName, "GetItemLegacy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cad.ActionName, "GetItemsByUserIdLegacy", StringComparison.OrdinalIgnoreCase)
           // || string.Equals(cad.ActionName, "GetVideoStream", StringComparison.OrdinalIgnoreCase)
           // || string.Equals(cad.ActionName, "GetItemSegments", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cad.ActionName, "GetPlaybackInfo", StringComparison.OrdinalIgnoreCase);
    }

    private void ReplaceGuid(ActionExecutingContext ctx, Guid value)
    {
                     //  _log.LogInformation("called");
        var rd = ctx.RouteData.Values;
        foreach (var key in new[] { "id", "Id", "ID", "itemId", "ItemId", "ItemID" })
        {
            if (rd.TryGetValue(key, out var raw) && raw is not null)
            {
                _log.LogDebug("Replacing route {Key} {Old} → {New}", key, raw, value);
                rd[key] = value.ToString();
                ctx.ActionArguments[key] = value;
            }
        }

        // Expose for downstream consumers if needed
        ctx.HttpContext.Items["GuidResolved"] = value;
    }

    private bool TryBuildRedirect(ActionExecutingContext ctx, out string url)
    {
        url = string.Empty;

        if (ctx.ActionDescriptor is not ControllerActionDescriptor cad)
            return false;

        var routeValues = new RouteValueDictionary(ctx.RouteData.Values);

        routeValues["controller"] = cad.ControllerName;
        routeValues["action"] = cad.ActionName;

        var path = _links.GetPathByAction(
            httpContext: ctx.HttpContext,
            action: cad.ActionName,
            controller: cad.ControllerName,
            values: routeValues);

        if (string.IsNullOrEmpty(path))
            return false;

        var qs = ctx.HttpContext.Request.QueryString; // preserve current query
        url = path + qs;
        return true;
    }

}