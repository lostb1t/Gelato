using Gelato.Common;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

public class InsertActionFilter : IAsyncActionFilter, IOrderedFilter {
    private readonly ILogger<InsertActionFilter> _log;
    private readonly GelatoManager _manager;
    private readonly KeyLock _lock = new();
    private readonly IUserManager _userManager;
    public int Order => 1;

    public InsertActionFilter(
        GelatoManager manager,
        IUserManager userManager,
        ILogger<InsertActionFilter> log
    ) {
        _manager = manager;
        _userManager = userManager;
        _log = log;
    }

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    ) {
        if (
            !ctx.IsInsertableAction()
            || !ctx.TryGetRouteGuid(out var guid)
            || !ctx.TryGetUserId(out var userId)
            || _userManager.GetUserById(userId) is not User user
            || _manager.GetStremioMeta(guid) is not StremioMeta stremioMeta
        ) {
            await next();
            return;
        }

        // Get root folder
        var isSeries = stremioMeta.Type == StremioMediaType.Series;
        var root = isSeries
            ? _manager.TryGetSeriesFolder(userId)
            : _manager.TryGetMovieFolder(userId);
        if (root is null) {
            _log.LogWarning("No {Type} folder configured", isSeries ? "Series" : "Movie");
            await next();
            return;
        }

        var item = _manager.IntoBaseItem(stremioMeta);
        var existing = _manager.FindExistingItem(item, user);
        if (existing is not null) {
            _log.LogInformation(
                "Media already exists; redirecting to canonical id {Id}",
                existing.Id
            );
            ctx.ReplaceGuid(existing.Id);
            await next();
            return;
        }

        // Fetch full metadata
        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        var meta = await cfg.stremio.GetMetaAsync(
            stremioMeta.ImdbId ?? stremioMeta.Id,
            stremioMeta.Type
        );
        if (meta is null) {
            _log.LogError(
                "aio meta not found for {Id} {Type}, maybe try aiometadata as meta addon.",
                stremioMeta.Id,
                stremioMeta.Type
            );
            await next();
            return;
        }

        // Insert the item
        var baseItem = await InsertMetaAsync(guid, root, meta, user);
        if (baseItem is not null) {
            ctx.ReplaceGuid(baseItem.Id);
            _manager.RemoveStremioMeta(guid);
        }

        await next();
    }

    public async Task<BaseItem?> InsertMetaAsync(
        Guid guid,
        Folder root,
        StremioMeta meta,
        User user
    ) {
        BaseItem? baseItem = null;
        var created = false;

        await _lock.RunQueuedAsync(
            guid,
            async ct => {
                meta.Guid = guid;
                (baseItem, created) = await _manager.InsertMeta(
                    root,
                    meta,
                    user,
                    false,
                    true,
                    meta.Type is StremioMediaType.Series,
                    ct
                );
            }
        );

        if (baseItem is not null && created)
            _log.LogInformation("inserted new media: {Name}", baseItem.Name);

        return baseItem;
    }
}
