using Gelato.Decorators;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Gelato.Filters;

/// <summary>
/// Drops what <see cref="GelatoItemRepository"/> hides from listings from a listing response that
/// never reached the item repository: the titles opened from search and never played,
/// unreleased Gelato items, and the automatic collections left with fewer than two listed movies.
/// </summary>
/// <remarks>
/// <see cref="GelatoItemRepository"/> excludes them from the query, which covers every recursive
/// listing. A non-recursive one is answered from the folder's children, which Jellyfin holds in
/// memory and filters in process (Folder.GetItemsInternal), so the query the filter rewrites is
/// never asked: browsing a library folder listed items the library view hides.
/// </remarks>
public sealed class HiddenListingFilter(IItemRepository itemRepository) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        var executed = await next();
        if (executed.Exception is not null && !executed.ExceptionHandled)
            return;

        DropPending(ctx, executed);
        DropUnreleased(ctx, executed);
        DropThinCollections(ctx, executed);
    }

    private void DropThinCollections(ActionExecutingContext ctx, ActionExecutedContext executed)
    {
        if (
            !ctx.HttpContext.IsApiListing()
            || ctx.HttpContext.HasExplicitItemIds()
            || itemRepository is not GelatoItemRepository repository
            || executed.Result is not ObjectResult result
        )
            return;

        var hidden = repository.GetThinCollectionIds();
        if (hidden.Count > 0)
            Drop(result, i => hidden.Contains(i.Id));
    }

    private void DropPending(ActionExecutingContext ctx, ActionExecutedContext executed)
    {
        if (
            GelatoPlugin.Instance?.Configuration is not { HideUnplayedSearchTitles: true }
            || !ctx.HttpContext.IsApiListing()
            || ctx.HttpContext.IsSearchRequest()
            || ctx.HttpContext.HasExplicitItemIds()
            || itemRepository is not GelatoItemRepository repository
            || executed.Result is not ObjectResult result
        )
            return;

        var hidden = repository.GetPendingIds();
        if (hidden.Count == 0 || GelatoItemRepository.IsInsidePending(ctx.HttpContext, hidden))
            return;

        Drop(result, i => hidden.Contains(i.Id));
    }

    private void DropUnreleased(ActionExecutingContext ctx, ActionExecutedContext executed)
    {
        if (
            GelatoPlugin.Instance?.Configuration is not { FilterUnreleased: true } cfg
            || !ctx.HttpContext.IsApiListing()
            || itemRepository is not GelatoItemRepository repository
            || executed.Result is not ObjectResult result
        )
            return;

        var hidden = repository.GetUnreleasedIds(cfg.FilterUnreleasedBufferDays);
        if (hidden.Count == 0)
            return;

        // The Upcoming list is made of unaired episodes, all of them in the set: it keeps those of
        // a released series and drops those of a series the filter hides.
        Func<BaseItemDto, bool> isHidden = ctx.HttpContext.IsUpcomingEpisodes()
            ? i => i.SeriesId is { } series && hidden.Contains(series)
            : i => hidden.Contains(i.Id);

        Drop(result, isHidden);
    }

    private static void Drop(ObjectResult result, Func<BaseItemDto, bool> isHidden)
    {
        switch (result.Value)
        {
            case QueryResult<BaseItemDto> query when query.Items.Count > 0:
                var kept = query.Items.Where(i => !isHidden(i)).ToArray();
                if (kept.Length == query.Items.Count)
                    return;
                result.Value = new QueryResult<BaseItemDto>(
                    query.StartIndex,
                    query.TotalRecordCount - (query.Items.Count - kept.Length),
                    kept
                );
                return;

            case IReadOnlyList<BaseItemDto> items when items.Count > 0:
                var list = items.Where(i => !isHidden(i)).ToArray();
                if (list.Length != items.Count)
                    result.Value = list;
                return;
        }
    }
}
