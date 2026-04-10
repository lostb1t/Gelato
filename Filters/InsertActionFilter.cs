using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

public class InsertActionFilter(
    GelatoManager manager,
    IUserManager userManager,
    ILibraryManager libraryManager,
    ILogger<InsertActionFilter> log
) : IAsyncActionFilter, IOrderedFilter
{
    private readonly KeyLock _lock = new();
    public int Order => 1;

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        if (
            !ctx.IsInsertableAction()
            || !ctx.TryGetRouteGuid(out var guid)
            || !ctx.TryGetUserId(out var userId)
            || userManager.GetUserById(userId) is not { } user
        )
        {
            await next();
            return;
        }

        // Handle local (non-gelato) series: sync or clean tree on demand
        if (libraryManager.GetItemById(guid) is Series localSeries && !localSeries.IsGelato())
        {
            await HandleLocalSeriesAsync(userId, localSeries, ctx.HttpContext.RequestAborted);
            await next();
            return;
        }

        if (manager.GetStremioMeta(guid) is not { } stremioMeta)
        {
            await next();
            return;
        }

        // Get root folder
        var isSeries = stremioMeta.Type == StremioMediaType.Series;
        var root = isSeries
            ? manager.TryGetSeriesFolder(userId)
            : manager.TryGetMovieFolder(userId);
        if (root is null)
        {
            log.LogWarning("No {Type} folder configured", isSeries ? "Series" : "Movie");
            await next();
            return;
        }

        if (manager.IntoBaseItem(stremioMeta) is { } item)
        {
            var existing = manager.FindExistingItem(item, user);
            if (existing is not null)
            {
                log.LogInformation(
                    "Media already exists; redirecting to canonical id {Id}",
                    existing.Id
                );
                ctx.ReplaceGuid(existing.Id);
                await next();
                return;
            }
        }

        // Fetch full metadata
        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        var meta = await cfg.Stremio.GetMetaAsync(
            stremioMeta.ImdbId ?? stremioMeta.Id,
            stremioMeta.Type
        );
        if (meta is null)
        {
            log.LogError(
                "aio meta not found for {Id} {Type}, maybe try aiometadata as meta addon.",
                stremioMeta.Id,
                stremioMeta.Type
            );
            await next();
            return;
        }

        // Insert the item
        var baseItem = await InsertMetaAsync(guid, root, meta, user);
        if (baseItem is not null)
        {
            ctx.ReplaceGuid(baseItem.Id);
            manager.RemoveStremioMeta(guid);
        }

        await next();
    }

    private async Task HandleLocalSeriesAsync(Guid userId, Series series, CancellationToken ct)
    {
        var cfg = GelatoPlugin.Instance!.GetConfig(userId);

        if (cfg.ExtendLocalSeriesTrees)
        {
            var alreadySynced =
                series.Tags?.Contains(GelatoManager.TreeSyncedTag, StringComparer.OrdinalIgnoreCase)
                ?? false;
            if (alreadySynced)
                return;

            if (cfg.Stremio is not { } stremio)
                return;

            log.LogInformation(
                "InsertActionFilter: syncing local series tree for {Name} ({Id})",
                series.Name,
                series.Id
            );

            var meta = await stremio.GetMetaAsync(series).ConfigureAwait(false);
            if (meta is null)
                return;

            await manager
                .SyncSeriesTreesAsync(cfg, meta, ct, existingSeries: series)
                .ConfigureAwait(false);
        }
        else
        {
            // Setting disabled — clean any virtual items that may exist for this series
            var virtualItems = libraryManager
                .GetItemList(
                    new InternalItemsQuery
                    {
                        IncludeItemTypes = [BaseItemKind.Season, BaseItemKind.Episode],
                        AncestorIds = [series.Id],
                    }
                )
                .Where(item =>
                    item.IsGelato()
                    && item.Path?.StartsWith("gelato://", StringComparison.OrdinalIgnoreCase)
                        == true
                )
                .ToList();

            if (virtualItems.Count == 0)
                return;

            log.LogInformation(
                "InsertActionFilter: cleaning {Count} virtual item(s) from local series {Name}",
                virtualItems.Count,
                series.Name
            );

            var episodes = virtualItems.OfType<Episode>().ToList();
            var seasons = virtualItems.OfType<Season>().ToList();
            foreach (var item in episodes.Concat<BaseItem>(seasons))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    libraryManager.DeleteItem(
                        item,
                        new DeleteOptions { DeleteFileLocation = false }
                    );
                }
                catch (Exception ex)
                {
                    log.LogError(
                        ex,
                        "InsertActionFilter: failed to delete {Name} ({Id})",
                        item.Name,
                        item.Id
                    );
                }
            }
        }
    }

    public async Task<BaseItem?> InsertMetaAsync(
        Guid guid,
        Folder root,
        StremioMeta meta,
        User user
    )
    {
        BaseItem? baseItem = null;
        var created = false;

        await _lock.RunQueuedAsync(
            guid,
            async ct =>
            {
                meta.Guid = guid;
                (baseItem, created) = await manager.InsertMeta(
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
            log.LogInformation("inserted new media: {Name}", baseItem.Name);

        return baseItem;
    }
}
