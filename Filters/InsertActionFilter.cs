using System;
using System.Linq;
using System.Threading.Tasks;
using Gelato.Common;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

public class InsertActionFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly ILibraryManager _library;
    private readonly GelatoStremioProvider _stremioProvider;
    private readonly ILogger<InsertActionFilter> _log;
    private readonly GelatoManager _manager;
    private readonly KeyLock _lock = new();

    public int Order => 1;

    public InsertActionFilter(
        ILibraryManager library,
        GelatoManager manager,
        GelatoStremioProvider stremioProvider,
        ILogger<InsertActionFilter> log
    )
    {
        _library = library;
        _stremioProvider = stremioProvider;
        _manager = manager;
        _log = log;
    }

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        if (
            !ctx.IsInsertableAction()
            || !ctx.TryGetRouteGuid(out var guid)
            || _manager.GetStremioMeta(guid) is not StremioMeta stremioMeta
        )
        {
            await next();
            return;
        }

        // Check if already exists
        var item = _stremioProvider.IntoBaseItem(stremioMeta);
        var existing = FindExistingItem(item);
        if (existing is not null)
        {
            _log.LogInformation(
                "Media already exists; redirecting to canonical id {Id}",
                existing.Id
            );
            ctx.ReplaceGuid(existing.Id);
            await next();
            return;
        }

        // Get root folder
        var isSeries = stremioMeta.Type == StremioMediaType.Series;
        var root = isSeries ? _manager.TryGetSeriesFolder() : _manager.TryGetMovieFolder();
        if (root is null)
        {
            _log.LogWarning("No {Type} folder configured", isSeries ? "Series" : "Movie");
            await next();
            return;
        }

        // Fetch full metadata
        var meta = await _stremioProvider.GetMetaAsync(stremioMeta.ImdbId ?? stremioMeta.Id, stremioMeta.Type);
        if (meta is null)
        {
            _log.LogError(
                "aio meta not found for {Id} {Type}, maybe try aiometadata as meta addon.",
                stremioMeta.Id,
                stremioMeta.Type
            );
            await next();
            return;
        }

        // Insert the item
        var baseItem = await InsertMetaAsync(guid, root, meta);
        if (baseItem is not null)
        {
            ctx.ReplaceGuid(baseItem.Id);
            _manager.RemoveStremioMeta(guid);
        }

        await next();
    }

    public BaseItem? FindExistingItem(BaseItem item)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { item.GetBaseItemKind() },
            HasAnyProviderId = item.ProviderIds,
            Recursive = true,
            IsDeadPerson = true, // skip filter marker
        };

        return _library.GetItemList(query).OfType<BaseItem>().FirstOrDefault();
    }

    public async Task<BaseItem?> InsertMetaAsync(Guid guid, Folder root, StremioMeta meta)
    {
        BaseItem? baseItem = null;
        var created = false;

        await _lock.RunQueuedAsync(
            guid,
            async ct =>
            {
                meta.Guid = guid;
                (baseItem, created) = await _manager.InsertMeta(root, meta, false, true, ct);
            }
        );

        if (baseItem is not null && created)
            _log.LogInformation("inserted new media: {Name}", baseItem.Name);

        return baseItem;
    }
}
