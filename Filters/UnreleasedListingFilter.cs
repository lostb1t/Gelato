using Gelato.Decorators;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Gelato.Filters;

/// <summary>
/// Drops unreleased Gelato items from a listing response that never reached the item repository.
/// </summary>
/// <remarks>
/// <see cref="GelatoItemRepository"/> excludes them from the query, which covers every recursive
/// listing. A non-recursive one is answered from the folder's children, which Jellyfin holds in
/// memory and filters in process (Folder.GetItemsInternal), so the query the filter rewrites is
/// never asked: browsing a library folder listed items the library view hides.
/// </remarks>
public sealed class UnreleasedListingFilter(IItemRepository itemRepository) : IAsyncActionFilter
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
            GelatoPlugin.Instance?.Configuration is not { FilterUnreleased: true } cfg
            || !ctx.HttpContext.IsApiListing()
            || itemRepository is not GelatoItemRepository repository
            || executed.Result is not ObjectResult result
        )
            return;

        var unreleased = repository.GetUnreleasedIds(cfg.FilterUnreleasedBufferDays);
        if (unreleased.Length == 0)
            return;

        var hidden = new HashSet<Guid>(unreleased);
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
