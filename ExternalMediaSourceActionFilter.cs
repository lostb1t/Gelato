using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Jellyfin.Plugin.ExternalMedia.Common;
using MediaBrowser.Model.IO;
using MediaBrowser.Controller.Dto;
using Jellyfin.Data.Enums;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;
using Microsoft.AspNetCore.Http;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.ExternalMedia;

public class ExternalMediaSourceActionFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly ILibraryManager _library;
    private readonly IItemRepository _repo;
    private readonly IMediaSourceManager _mediaSources;
    private readonly IDtoService _dtoService;
    private readonly ExternalMediaStremioProvider _stremioProvider;
    private readonly ILogger<ExternalMediaSourceActionFilter> _log;
    private readonly ExternalMediaManager _manager;
    private readonly ExternalMediaSeriesManager _seriesManager;
    private readonly IMediaSourceManager _sourceManager;
    // private readonly ExternalMediaRefresh _refresh;

    private readonly IProviderManager _provider;
    private readonly IFileSystem _fileSystem;
    private readonly ILibraryMonitor _libraryMonitor;

    public int Order => throw new NotImplementedException();

    public ExternalMediaSourceActionFilter(
        ILibraryManager library,
        //  ExternalMediaRefresh refresh,
        IFileSystem fileSystem,
        IItemRepository repo,
        IMediaSourceManager mediaSources,
        ExternalMediaManager manager,
          ExternalMediaSeriesManager seriesManager,
        IDtoService dtoService,
        ExternalMediaStremioProvider stremioProvider,
        IProviderManager provider,
        ILibraryMonitor libraryMonitor,
        IMediaSourceManager sourceManager,
        ILogger<ExternalMediaSourceActionFilter> log)
    {
        _library = library;
        _sourceManager = sourceManager;
        //  _refresh = refresh;
        _repo = repo;
        _mediaSources = mediaSources;
        _dtoService = dtoService;
        _provider = provider;
        _stremioProvider = stremioProvider;
        _fileSystem = fileSystem;
        _manager = manager;
        _seriesManager = seriesManager;
        _log = log;
        _libraryMonitor = libraryMonitor;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        // _log.LogInformation("ExternalMedia: OnActionExecutionAsync");
        // _log.LogInformation("ExternalMedia: No {Type} folder configured", isSeries ? "Series" : "Movie");
        var req = ctx.HttpContext.Request;
        _log.LogInformation("ExternalMedia: Requested path = {Path}{Query}", req.Path, req.QueryString);
        if (!_manager.IsItemsAction(ctx))
        {
            await next();
            return;

        }

        ctx.HttpContext.Items.TryGetValue("MediaSourceGuid", out var MediaSourceGuid);
        _log.LogInformation("Guid from HttpContext.Items = {Guid}", MediaSourceGuid);

        // if (ctx.HttpContext.Items.TryGetValue("MediaSourceGuid", out var MediaSourceGuid) && raw is Guid g)
        // {
        //     _log.LogInformation("Guid from HttpContext.Items = {Guid}", g);
        // }
        // else
        // {
        //     _log.LogDebug("Guid not set in HttpContext.Items");
        // }
        // // var req = ctx.HttpContext.Request;
        // _log.LogInformation("ExternalMedia: Requested path = {Path}{Query}", req.Path, req.QueryString);


        if (!_manager.TryGetRouteGuid(ctx, out var guid))
        {
            await next();
            return;
        }
        ;

        // if (StremioId.TryParseFromGuid(guid, out var stremioId, out var mt, out var extId))
        // {
        //     _log.LogDebug("StremioId {Id}, Type {Type}, External {Ext}", stremioId, mt, extId);
        //     _manager.ReplaceGuid(ctx, guid);
        // }

        // _log.LogInformation("ExternalMedia: Found route guid {Guid}", guidString);
        // TryParseSourceId(guidString, out var guid, out var index);
        // if (index is not null)
        // {
        //     _manager.ReplaceGuid(ctx, guid);
        // }
        // var item = _library.GetItemById(itemDto.Id);
        // if (item is null)
        // {
        //     return;
        // }
        // _log.LogWarning("ExternalMedia: Invalid stremio id in guid {Guid}", guid);
        // existing
        var executed = await next();
        // PostPatchItemsDtosIfAny(executed);
        if (executed?.Result is not Microsoft.AspNetCore.Mvc.ObjectResult obj || obj.Value is not BaseItemDto itemDto)
            return;
        // var r = itemDto as BaseItem;

        var item = _library.GetItemById(itemDto.Id);
        if (item is null)
        {
            await next();
            return;
        }
        var sources = await _mediaSources.GetPlaybackMediaSources(
            item,
            null,
            allowMediaProbe: false,  // should enable this at one point.
            enablePathSubstitution: true,
            cancellationToken: CancellationToken.None
        ).ConfigureAwait(false);

        itemDto.MediaSources = sources
        // remove the placeholder
        .Where(src => src.Id != item.Id.ToString("N"))
        .Select(src =>
        {
            //if (Guid.TryParse(src.Id, out var msid))
            //if (MediaSourceGuid is null)
            //{
                //src.Id = $"{item.Id:N}::{src.Id:N}";
            //}
            return src;
        })
        .ToArray();
        // not sure howto set these otherwise
        itemDto.CanDownload = true;
        // itemDto.LocationType = "Remote";
        // itemDto.Container = "mp4";
        // itemDto.LocationType = LocationType.Remote;
        _log.LogInformation("ExternalMedia: Existing item, returning {0} media sources", sources.Count);
        // t.GetMediaSources =
        obj.Value = itemDto;
        // return;


        // await next();
    }

    public static bool TryParseSourceId(string sourceId, out Guid itemId, out int? index)
    {
        itemId = Guid.Empty;
        index = null;

        if (string.IsNullOrWhiteSpace(sourceId))
            return false;

        var parts = sourceId.Split(new[] { "::" }, StringSplitOptions.None);

        if (parts.Length == 1)
        {
            // just a GUID
            return Guid.TryParse(parts[0], out itemId);
        }

        if (parts.Length == 2 &&
            Guid.TryParse(parts[0], out itemId) &&
            int.TryParse(parts[1], out var idx))
        {
            index = idx;
            return true;
        }

        return false;
    }

}