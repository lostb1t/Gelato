using Gelato.Decorators;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Gelato.Filters;

/// <summary>
/// Drops the titles opened from search and never played from a listing response that never
/// reached the item repository, as <see cref="UnreleasedListingFilter"/> does for unreleased ones:
/// a non-recursive listing of a library folder is answered from its cached children.
/// </summary>
public sealed class PendingListingFilter(IItemRepository itemRepository) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        var executed = await next();
        if (executed.Exception is not null && !executed.ExceptionHandled)
            return;

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

        switch (result.Value)
        {
            case QueryResult<BaseItemDto> query when query.Items.Count > 0:
                var kept = query.Items.Where(i => !hidden.Contains(i.Id)).ToArray();
                if (kept.Length == query.Items.Count)
                    return;
                result.Value = new QueryResult<BaseItemDto>(
                    query.StartIndex,
                    query.TotalRecordCount - (query.Items.Count - kept.Length),
                    kept
                );
                return;

            case IReadOnlyList<BaseItemDto> items when items.Count > 0:
                var list = items.Where(i => !hidden.Contains(i.Id)).ToArray();
                if (list.Length != items.Count)
                    result.Value = list;
                return;
        }
    }
}
