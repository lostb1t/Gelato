using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

public sealed class DeleteResourceFilter(
    ILibraryManager library,
    GelatoManager manager,
    IUserManager userManager,
    ILogger<DeleteResourceFilter> log
) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        // Only intercept DeleteItem actions with valid user
        if (
            ctx.GetActionName() != "DeleteItem"
            || !ctx.TryGetRouteGuid(out var guid)
            || !ctx.TryGetUserId(out var userId)
            || userManager.GetUserById(userId) is not { } user
        )
        {
            await next();
            return;
        }

        var item = library.GetItemById<BaseItem>(guid, user);

        // Only handle Gelato items that user can delete
        if (item is null || !item.IsGelato() || !manager.CanDelete(item, user))
        {
            await next();
            return;
        }

        // Handle deletion and return 204 No Content
        DeleteItem(item);
        ctx.Result = new NoContentResult();
    }

    private void DeleteItem(BaseItem item)
    {
        if (item is Video video && item.IsPrimaryVersion())
        {
            // Its stream rows first, with their watch state: Jellyfin would delete the linked rows
            // along with the movie, but park their user data. Only this item's rows: another item
            // of the same title (a local movie, another user's folder) keeps its own.
            manager.DeleteStreamRows(video, manager.GetStreamRows(video), CancellationToken.None);
        }

        log.LogInformation("Deleting {Name} ({Id})", item.Name, item.Id);
        library.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, true);
    }
}
