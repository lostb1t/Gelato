using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
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
        // An add to a collection or a playlist, or the creation of one, names its items in a list,
        // and nothing has to have opened them: a multi-select or a script sends search results as
        // they are. Each one goes into the library first, so the redirect below knows an item for
        // every id. Jellyfin answered 400 for such a collection add, and 204 for a playlist add
        // that added nothing.
        if (ctx.IsGroupAddAction())
            await MaterializeIdListAsync(ctx);

        // A client that opened a search result keeps the result's id in its page URL: the
        // reload, images, seasons, episodes, similar items and playback all name it. Every such
        // id goes to the item the result became, whatever the action.
        if (ctx.RedirectGuids(manager.GetInsertedId))
        {
            await next();
            return;
        }

        var insertable = ctx.IsInsertableAction();
        if (
            (!insertable && !ctx.IsCanonicalIdRead())
            || !ctx.TryGetRouteGuid(out var guid)
            || !ctx.TryGetUserId(out var userId)
            || userManager.GetUserById(userId) is not { } user
        )
        {
            await next();
            return;
        }

        // Handle local (non-gelato) series: sync or clean tree on demand
        if (
            insertable
            && libraryManager.GetItemById(guid) is Series localSeries
            && !localSeries.IsGelato()
        )
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

        if (FindExisting(guid, stremioMeta, user) is { } existing)
        {
            ctx.ReplaceGuid(existing.Id);
            await next();
            return;
        }

        // A read answers for what the library has; it never puts a title in. The result stays a
        // search result until something opens it.
        if (!insertable)
        {
            await next();
            return;
        }

        // A played write reaches a series' episodes, and the tree it just created is still growing:
        // the metadata refresh brings the episodes the first pass did not have. The answer goes out
        // without waiting for it (that took up to a minute on a long series), and the state is
        // applied again behind it, by the same call that runs the refresh the insert would queue.
        var wantsPlayed =
            stremioMeta.Type == StremioMediaType.Series ? ctx.WantsPlayedState() : null;
        var baseItem = await InsertAsync(
            guid,
            stremioMeta,
            userId,
            user,
            refreshItem: wantsPlayed is null,
            streamSyncFollows: ctx.ListsMediaSources()
        );
        if (baseItem is not null)
            ctx.ReplaceGuid(baseItem.Id);

        await next();

        if (baseItem is not null && wantsPlayed is bool played)
        {
            manager.RefreshAndReapplyPlayedState(baseItem, user, played);
        }
    }

    private async Task HandleLocalSeriesAsync(Guid userId, Series series, CancellationToken ct)
    {
        var cfg = GelatoPlugin.Instance!.GetConfig(userId);

        if (cfg.ExtendLocalSeriesTrees)
        {
            if (manager.HasExtendedTree(series))
            {
                manager.RemoveShadowedEpisodes(series, ct);
                return;
            }

            // Extended on a later visit: the scan has not numbered the series' own episodes yet,
            // and Gelato would fill the slots they are about to hold.
            if (manager.IsBeingScanned(series))
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
            manager.CleanVirtualTreeItem(series, ct);
        }
    }

    public async Task<BaseItem?> InsertMetaAsync(
        Guid guid,
        Folder root,
        StremioMeta meta,
        User user,
        bool refreshItem = true
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
                    refreshItem,
                    meta.Type is StremioMediaType.Series,
                    ct
                );
            }
        );

        if (baseItem is not null && created)
            log.LogInformation("inserted new media: {Name}", baseItem.Name);

        return baseItem;
    }

    /// <summary>
    /// Puts every search result among the ids of the request into the library, unless it is
    /// there already. An id that is no search result, or one that cannot be put in, is left as
    /// it is and reaches Jellyfin unchanged.
    /// </summary>
    private async Task MaterializeIdListAsync(ActionExecutingContext ctx)
    {
        if (!ctx.TryGetUserId(out var userId) || userManager.GetUserById(userId) is not { } user)
            return;

        foreach (var guid in ctx.GetIdList())
        {
            if (
                manager.GetInsertedId(guid) is not null
                || manager.GetStremioMeta(guid) is not { } stremioMeta
            )
                continue;

            if (FindExisting(guid, stremioMeta, user) is null)
                await InsertAsync(guid, stremioMeta, userId, user);
        }
    }

    /// <summary>
    /// The library item a search result already is, remembered for the redirect.
    /// </summary>
    private BaseItem? FindExisting(Guid guid, StremioMeta stremioMeta, User user)
    {
        if (
            manager.IntoBaseItem(stremioMeta) is not { } item
            || manager.FindExistingItem(item, user) is not { } existing
        )
            return null;

        log.LogInformation("Media already exists; redirecting to canonical id {Id}", existing.Id);
        manager.RememberInsertedId(guid, existing.Id);
        return existing;
    }

    /// <summary>
    /// Puts a search result into the library and remembers the item it became.
    /// </summary>
    private async Task<BaseItem?> InsertAsync(
        Guid guid,
        StremioMeta stremioMeta,
        Guid userId,
        User user,
        bool refreshItem = true,
        bool streamSyncFollows = false
    )
    {
        // Get root folder
        var isSeries = stremioMeta.Type == StremioMediaType.Series;
        var root = isSeries
            ? manager.TryGetSeriesFolder(userId)
            : manager.TryGetMovieFolder(userId);
        if (root is null)
        {
            log.LogWarning("No {Type} folder configured", isSeries ? "Series" : "Movie");
            return null;
        }

        // The answer this request builds lists the item's sources, which syncs its streams:
        // they are asked for now, next to the meta, instead of after it.
        if (streamSyncFollows)
            manager.StartStreamSyncAhead(stremioMeta, userId);

        // Fetch full metadata
        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        var meta = await cfg.Stremio.GetMetaAsync(stremioMeta);
        if (meta is null)
        {
            log.LogError(
                "aio meta not found for {Id} {Type}, maybe try aiometadata as meta addon.",
                stremioMeta.Id,
                stremioMeta.Type
            );
            return null;
        }

        var baseItem = await InsertMetaAsync(guid, root, meta, user, refreshItem);
        if (baseItem is not null)
        {
            manager.RememberInsertedId(guid, baseItem.Id);
            manager.RemoveStremioMeta(guid);
        }

        return baseItem;
    }
}
