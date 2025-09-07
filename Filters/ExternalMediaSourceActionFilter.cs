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
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Jellyfin.Database.Implementations.Entities.Libraries;
using Microsoft.AspNetCore.Mvc.Controllers;

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
    private readonly ExternalMediaSourceProvider _externalMediaSourceProvider;

    public int Order => 1;

    public ExternalMediaSourceActionFilter(
        // ExternalMediaSourceProvider externalMediaSourceProvider,
        IEnumerable<IMediaSourceProvider> providers,
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
        // ILibraryMonitor libraryMonitor,
        IMediaSourceManager sourceManager,
        ILogger<ExternalMediaSourceActionFilter> log)
    {
        _library = library;
        // _externalMediaSourceProvider = externalMediaSourceProvider;
        _externalMediaSourceProvider = providers.OfType<ExternalMediaSourceProvider>().FirstOrDefault()
     ?? throw new InvalidOperationException("ExternalMediaSourceProvider not registered.");
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
        // _libraryMonitor = libraryMonitor;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        var req = ctx.HttpContext.Request;
        _log.LogInformation("ExternalMedia: Requested path = {Path}{Query}", req.Path, req.QueryString);

        if (!_manager.IsItemsAction(ctx))
        {
            await next();
            return;
        }


        // this also fails if there are multiple ids
        if (!_manager.TryGetRouteGuid(ctx, out var guid))
        {
            // _log.LogInformation("ExternalMedia: No route guid");
            await next();
            return;
        }

        // i think this only happens with the web client. For some reason makes a extra request to the stream
        var stremioUri = _manager.GetStremioUri(guid);

        if (ctx.ActionDescriptor is not ControllerActionDescriptor cad)
        {
            await next();
            return;
        }
        // _log.LogInformation("ExternalMedia: Found route guid {Guid}, isUri={IsUri}", guid, stremioUri.ToString());
        var isStream = stremioUri is not null && stremioUri.StreamId is not null;
        var isList = cad.ActionName == "GetItemList" || cad.ActionName == "GetItemsByUserIdLegacy";
        BaseItem? item = null;

        // get the base
        if (isStream)
        {
            item = _library.GetItemList(new InternalItemsQuery
            {
                // ParentId = parent.Id,
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                HasAnyProviderId = new Dictionary<string, string> { ["stremio"] = stremioUri.ToString() },
                Recursive = true
            })
            .OfType<Video>()
            .FirstOrDefault();
        }
        else
        {
            item = _library.GetItemById(guid);
        }

        if (item is null) return;

        var ct = ctx.HttpContext.RequestAborted;
        // _log.LogInformation("ExternalMedia: Processing response object of type {Type}", obj.Value.GetType().FullName);
        async Task<BaseItemDto> ProcessOneAsync(BaseItem item, CancellationToken token)
        {
            var dto = _dtoService.GetBaseItemDto(
                item,
                new DtoOptions(),
                user: null
            );

            var kind = item.GetBaseItemKind();

            if (kind is not BaseItemKind.Movie && kind is not BaseItemKind.Episode)
            {
                return dto;
            }

            if (!_manager.IsStremioProvider(item))
            {
                return dto;
            }

            var sources = await _externalMediaSourceProvider.GetMediaSources(
                item,
                allowMediaProbe: false,
                ct: CancellationToken.None
            ).ConfigureAwait(false);

            // _log.LogInformation("ExternalMedia: Processing item {Name} ({Id})", item.Name, item.Id);

            dto.MediaSources = sources
                .Where(src => !string.Equals(src.Id, item.Id.ToString("N"), StringComparison.OrdinalIgnoreCase))
                .ToArray();

            dto.CanDownload = true;
            return dto;
        }

        var patchedItem = await ProcessOneAsync(item, ct);

        if (!isList)
        {
            ctx.Result = new ObjectResult(patchedItem);
            return;
        }

        // Query with list
        // QueryResult<BaseItemDto>
        if (isList)
        {
            var qr = new QueryResult<BaseItemDto>(
                0,
                1,
                new BaseItemDto[] { patchedItem }
            );

            ctx.Result = new ObjectResult(qr);
            return;
        }
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