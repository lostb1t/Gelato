using System;
using System.Linq;
using System.Threading.Tasks;
using Gelato.Common;
using Gelato.Configuration;
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
    private readonly GelatoStremioProviderFactory _stremioFactory;
    private readonly ILogger<InsertActionFilter> _log;
    private readonly GelatoManager _manager;
    private readonly KeyLock _lock = new();

    public int Order => 1;

    public InsertActionFilter(
        ILibraryManager library,
        GelatoManager manager,
        GelatoStremioProviderFactory stremioFactory,
        ILogger<InsertActionFilter> log
    )
    {
        _library = library;
        _stremioFactory = stremioFactory;
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
        var item = _manager.IntoBaseItem(stremioMeta);
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

        ctx.TryGetUserId(out var userId);

        // Get root folder
        var isSeries = stremioMeta.Type == StremioMediaType.Series;
        var root = isSeries
            ? _manager.TryGetSeriesFolder(userId)
            : _manager.TryGetMovieFolder(userId);
        if (root is null)
        {
            _log.LogWarning("No {Type} folder configured", isSeries ? "Series" : "Movie");
            await next();
            return;
        }

        // Fetch full metadata
        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        var meta = await cfg.stremio.GetMetaAsync(
            stremioMeta.ImdbId ?? stremioMeta.Id,
            stremioMeta.Type
        );
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
        var baseItem = await InsertMetaAsync(guid, root, meta, userId);
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
            IsVirtualItem = false,
            IsDeadPerson = true, // skip filter marker
        };

        return _library
            .GetItemList(query)
            .FirstOrDefault(x =>
            {
                if (x is null)
                    return false;

                if (x is Video v)
                {
                    return !_manager.IsStream(v);
                }

                return true;
            });
    }

    public async Task<BaseItem?> InsertMetaAsync(
        Guid guid,
        Folder root,
        StremioMeta meta,
        Guid userId
    )
    {
        BaseItem? baseItem = null;
        var created = false;

        await _lock.RunQueuedAsync(
            guid,
            async ct =>
            {
                meta.Guid = guid;
                (baseItem, created) = await _manager.InsertMeta(
                    root,
                    meta,
                    userId,
                    false,
                    false,
                    true,
                    ct
                );
            }
        );

        if (baseItem is not null && created)
            _log.LogInformation("inserted new media: {Name}", baseItem.Name);

        return baseItem;
    }
}
