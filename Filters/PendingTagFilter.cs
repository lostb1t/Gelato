using Gelato.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Gelato.Filters;

/// <summary>
/// Keeps <see cref="PendingSearchTitles.Tag"/> on a title the metadata editor saves.
/// </summary>
/// <remarks>
/// Clients never see the mark (DtoServiceDecorator leaves it out of the DTO, GelatoItemRepository
/// out of the filters' tag lists), and the editor saves the DTO it was given, whose tags replace
/// the item's: editing a hidden title would list it and keep it from being removed.
/// </remarks>
public sealed class PendingTagFilter(ILibraryManager libraryManager) : IAsyncActionFilter
{
    public Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        if (
            ctx.GetActionName() == "UpdateItem"
            && ctx.TryGetRouteGuid(out var id)
            && ctx.ActionArguments.TryGetValue("request", out var body)
            && body is BaseItemDto { Tags: { } tags } request
            && libraryManager.GetItemById(id) is { } item
            && PendingSearchTitles.IsPending(item)
            && !tags.Contains(PendingSearchTitles.Tag, StringComparer.OrdinalIgnoreCase)
        )
            request.Tags = [.. tags, PendingSearchTitles.Tag];

        return next();
    }
}
