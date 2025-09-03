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

namespace Jellyfin.Plugin.ExternalMedia;

public class ExternalMediaInsertActionFilter : IAsyncResourceFilter
{
    private readonly ILibraryManager _library;
    private readonly IItemRepository _repo;
    private readonly IMediaSourceManager _mediaSources;
    private readonly IDtoService _dtoService;
    private readonly ExternalMediaStremioProvider _stremioProvider;
    private readonly ILogger<ExternalMediaInsertActionFilter> _log;
    private readonly ExternalMediaManager _manager;
    private readonly ExternalMediaSeriesManager _seriesManager;
    // private readonly ExternalMediaRefresh _refresh;

    private readonly IProviderManager _provider;
    private readonly IFileSystem _fileSystem;
    private readonly ILibraryMonitor _libraryMonitor;

    public ExternalMediaInsertActionFilter(
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
        ILogger<ExternalMediaInsertActionFilter> log)
    {
        _library = library;
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

    public async Task OnResourceExecutionAsync(ResourceExecutingContext ctx, ResourceExecutionDelegate next)
    {

        // _log.LogInformation("ExternalMedia: No {Type} folder configured", isSeries ? "Series" : "Movie");

        if (!IsItemsAction(ctx))
        {
            await next();
            return;

        }
        var req = ctx.HttpContext.Request;
        _log.LogInformation("ExternalMedia: Requested path = {Path}{Query}", req.Path, req.QueryString);

        if (!TryGetRouteGuid(ctx, out var guid))
        {
            await next();
            return;
        }



        // declare in outer scope
        // declare once, in outer scope
        string stremioId = default!;
        StremioMediaType mediaType = default;
        string Id = default!;

        try
        {
            var decoded = GuidCodec.DecodeString(guid);      // whatever your guid codec returns
            stremioId = StremioId.FromCompactId(decoded);    // formerly Decode(...)

            var parsed = StremioId.Parse(stremioId);         // throws if invalid
            mediaType = parsed.MediaType;                   // assign to the outer vars
            Id = parsed.ExternalId;
        }
        catch
        {
            await next();
            return;
        }

        var found = FindByStremioId(stremioId) as Video;
        if (found is not null)
        {
            ReplaceGuid(ctx, found.Id);
            await next();
            return;
        }

        bool isSeries = mediaType == StremioMediaType.Series;
        var root = isSeries ? _manager.TryGetSeriesFolder() : _manager.TryGetMovieFolder();
        if (root is null)
        {
            _log.LogWarning("ExternalMedia: No {Type} folder configured", isSeries ? "Series" : "Movie");
            await next();
            return;
        }

        var meta = await _stremioProvider.GetMetaAsync(Id, mediaType).ConfigureAwait(false);
        if (meta is null)
        {
            _log.LogWarning("ExternalMedia: Meta not found for {0} {1}", Id, mediaType);
            await next();
            return;
        }

        var baseItem = _stremioProvider.IntoBaseItem(meta);
        if (baseItem is null || baseItem.ProviderIds is null || baseItem.ProviderIds.Count == 0)
        {
            _log.LogWarning("ExternalMedia: Missing provider ids, skipping");
            await next();
            return;
        }

        Func<CancellationToken, Task<bool>> saver = isSeries
            ? (async ct =>
            {
                await _seriesManager.CreateSeriesTreesAsync(root, meta, true, ct);
                StartSeriesRefreshDettached(root, meta);
                return true;
            })
            : (async ct =>
            {
                var streams = await _stremioProvider.GetStreamsAsync(baseItem).ConfigureAwait(false);
                var items = await _manager.SaveStreams(streams, root, meta, CancellationToken.None).ConfigureAwait(false);
                var primaryItem = _manager.GetPrimaryVersion(items);
                if (primaryItem is not null)
                {
                    ReplaceGuid(ctx, primaryItem.Id);
                }
                await _library.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);
                return true;
            });


        var ok = await saver(CancellationToken.None).ConfigureAwait(false);
        _log.LogInformation("ExternalMedia: saved media");

        await next();
    }

    public void StartSeriesRefreshDettached(Folder root, StremioMeta meta)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // wait 10 seconds before starting the refresh

                using var ct = new CancellationTokenSource();
                await _seriesManager.CreateSeriesTreesAsync(root, meta, false, CancellationToken.None);
                //await _refresh.RefreshAsync(root, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "ExternalMedia: background refresh failed for {Name}", root.Name);
            }
        });
    }

    private bool IsItemsAction(ResourceExecutingContext ctx)
    {
        if (ctx.ActionDescriptor is not ControllerActionDescriptor cad)
            return false;

        // _log.LogInformation("ExternalMedia: Action = {Action}", cad.ActionName);
        return string.Equals(cad.ActionName, "GetItems", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cad.ActionName, "GetItem", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cad.ActionName, "GetItemLegacy", StringComparison.OrdinalIgnoreCase)
            || string.Equals(cad.ActionName, "GetItemsByUserIdLegacy", StringComparison.OrdinalIgnoreCase);

    }

    static bool IsJellyfinWeb(HttpRequest req)
    {
        var h = req.Headers["X-Emby-Authorization"].ToString();
        // very forgiving parse
        return h.IndexOf("Client=Jellyfin Web", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool IsLegacyItemAction(ResourceExecutingContext ctx)
    {
        if (ctx.ActionDescriptor is not ControllerActionDescriptor cad)
            return false;
        return string.Equals(cad.ActionName, "GetItemsByUserIdLegacy", StringComparison.OrdinalIgnoreCase);

    }

    private bool TryGetRouteGuid(ResourceExecutingContext ctx, out Guid value)
    {
        value = Guid.Empty;

        // Skip legacy endpoint entirely
        //if (ctx.ActionDescriptor is ControllerActionDescriptor cad &&
        //    string.Equals(cad.ActionName, "GetItemLegacy", StringComparison.OrdinalIgnoreCase))
        //{
        //    return false;
        // }

        var rd = ctx.RouteData.Values;

        foreach (var key in new[] { "id", "Id", "ID", "itemId", "ItemId", "ItemID" })
        {
            if (rd.TryGetValue(key, out var raw) && raw is not null)
            {
                var s = raw.ToString();
                if (!string.IsNullOrWhiteSpace(s) && Guid.TryParse(s, out value))
                    return true;
            }
        }

        // Fallback: check query string "ids"
        var query = ctx.HttpContext.Request.Query;
        if (query.TryGetValue("ids", out var ids) && ids.Count == 1)
        {
            var s = ids[0];
            if (!string.IsNullOrWhiteSpace(s) && Guid.TryParse(s, out value))
                return true;
        }

        return false;
    }

    private void ReplaceGuid(ResourceExecutingContext ctx, Guid value)
    {
        // Replace route values
        var rd = ctx.RouteData.Values;
        foreach (var key in new[] { "id", "Id", "ID", "itemId", "ItemId", "ItemID" })
        {
            if (rd.TryGetValue(key, out var raw) && raw is not null)
            {
                _log.LogInformation("ExternalMedia: Replacing route {Key} {Old} → {New}", key, raw, value);
                ctx.RouteData.Values[key] = value.ToString();
            }
        }

        // Replace query string "ids"
        var request = ctx.HttpContext.Request;
        var parsed = QueryHelpers.ParseQuery(request.QueryString.Value ?? "");

        if (parsed.TryGetValue("ids", out var existing) && existing.Count == 1)
        {
            _log.LogInformation("ExternalMedia: Replacing query ids {Old} → {New}", existing[0], value);

            var dict = new Dictionary<string, StringValues>(parsed)
            {
                ["ids"] = new StringValues(value.ToString())
            };

            request.QueryString = QueryString.Create(dict);
        }
    }


    public Video? FindByStremioId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var lastSlash = id.LastIndexOf('/');
        var ext = lastSlash >= 0 ? id[(lastSlash + 1)..] : id;

        string providerKey;
        string providerValue;

        if (ext.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase))
        {
            providerKey = MetadataProvider.Tmdb.ToString();
            providerValue = ext.Substring("tmdb:".Length);
        }
        else if (ext.StartsWith("imdb:", StringComparison.OrdinalIgnoreCase))
        {
            providerKey = MetadataProvider.Imdb.ToString();
            providerValue = ext.Substring("imdb:".Length);
        }
        else if (ext.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            providerKey = MetadataProvider.Imdb.ToString();
            providerValue = ext;
        }
        else
        {
            return null;
        }

        var q = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            Limit = 1,
            HasAnyProviderId = new Dictionary<string, string> { [providerKey] = providerValue }
        };

        return _library.GetItemList(q).OfType<Video>().FirstOrDefault(i => i.MediaSourceCount > 1 && string.IsNullOrEmpty(i.PrimaryVersionId));
    }
}