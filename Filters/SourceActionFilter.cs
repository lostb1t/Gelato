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
using Gelato.Common;
using MediaBrowser.Model.IO;
using MediaBrowser.Controller.Dto;
using Jellyfin.Data.Enums;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Gelato.Filters;

// Todo: should probaplya override the mediasourcemanager and inject there instead of an actionfilter.
public class SourceActionFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly ILibraryManager _library;
    private readonly IItemRepository _repo;
    private readonly IMediaSourceManager _mediaSources;
    private readonly IDtoService _dtoService;
    private readonly GelatoStremioProvider _stremioProvider;
    private readonly ILogger<SourceActionFilter> _log;
    private readonly GelatoManager _manager;
    private readonly GelatoSeriesManager _seriesManager;
    private readonly IMediaSourceManager _sourceManager;
    private readonly IProviderManager _provider;
    private readonly IFileSystem _fileSystem;
    private readonly GelatoSourceProvider _externalMediaSourceProvider;

    public int Order => 1;

    public SourceActionFilter(
        // GelatoSourceProvider externalMediaSourceProvider,
        IEnumerable<IMediaSourceProvider> providers,
        ILibraryManager library,
        //  GelatoRefresh refresh,
        IFileSystem fileSystem,
        IItemRepository repo,
        IMediaSourceManager mediaSources,
        GelatoManager manager,
          GelatoSeriesManager seriesManager,
        IDtoService dtoService,
        GelatoStremioProvider stremioProvider,
        IProviderManager provider,
        // ILibraryMonitor libraryMonitor,
        IMediaSourceManager sourceManager,
        ILogger<SourceActionFilter> log)
    {
        _library = library;
        _externalMediaSourceProvider = providers.OfType<GelatoSourceProvider>().FirstOrDefault()
     ?? throw new InvalidOperationException("GelatoSourceProvider not registered.");
        _sourceManager = sourceManager;
        _repo = repo;
        _mediaSources = mediaSources;
        _dtoService = dtoService;
        _provider = provider;
        _stremioProvider = stremioProvider;
        _fileSystem = fileSystem;
        _manager = manager;
        _seriesManager = seriesManager;
        _log = log;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        // var req = ctx.HttpContext.Request;
        // _log.LogInformation("Gelato: Requested path = {Path}{Query}", req.Path, req.QueryString);

        if (!_manager.IsItemsAction(ctx))
        {
            await next();
            return;
        }

        // this also fails if there are multiple ids
        if (!_manager.TryGetRouteGuid(ctx, out var guid))
        {
            // _log.LogInformation("Gelato: No route guid");
            await next();
            return;
        }

        var stremioMeta = _manager.GetStremioMeta(guid);
        var stremioUri = _manager.GetStremioUri(guid);
        if (ctx.ActionDescriptor is not ControllerActionDescriptor cad)
        {
            await next();
            return;
        }

        var isStream = false;
        if (stremioMeta is not null)
        {
            stremioUri = StremioUri.FromMeta(stremioMeta);
            isStream = stremioUri.StreamId is not null;
        }
        else if (stremioUri is not null)
        {
            isStream = stremioUri.StreamId is not null;
        }
        // _log.LogInformation("Gelato: Action {Action}, Guid {Guid}, Stremio {Stremio}, IsStream {IsStream}", stremioMeta?.Id, guid, stremioUri?.ToString() ?? "null", isStream);

        var isList = cad.ActionName == "GetItemList" || cad.ActionName == "GetItemsByUserIdLegacy";
        BaseItem? item = null;

        // get the base
        if (isStream)
        {
            item = _library.GetItemList(new InternalItemsQuery
            {
                // ParentId = parent.Id,
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                HasAnyProviderId = new Dictionary<string, string> { ["stremio"] = stremioUri.ToBaseString() },
                Recursive = true
            })
            .OfType<Video>()
            .FirstOrDefault();
        }
        else
        {
            // issue... guid can also be from an media source. Cause jellyfin is retarted. We dont persist that so... yeah.
            item = _library.GetItemById(guid);
        }

        if (item is null) return;

        var ct = ctx.HttpContext.RequestAborted;
        // _log.LogInformation("Gelato: Processing response object of type {Type}", guid);
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

            // _log.LogInformation("Gelato: Processing item {Name} ({Id})", item.Name, item.Id);

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