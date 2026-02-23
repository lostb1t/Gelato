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
    ILogger<DeleteResourceFilter> log)
    : IAsyncActionFilter {
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
        if (item is null || !manager.IsGelato(item) || !manager.CanDelete(item, user))
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
        if (item.IsPrimaryVersion())
        {
            DeleteStreams(item);
        }
        else
        {
            log.LogInformation("Deleting {Name}", item.Name);
            library.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, true);
        }
    }

    private void DeleteStreams(BaseItem item)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [item.GetBaseItemKind()],
            HasAnyProviderId = new Dictionary<string, string> { { "Stremio", item.ProviderIds["Stremio"] } },
            Recursive = false,
            GroupByPresentationUniqueKey = false,
            GroupBySeriesPresentationUniqueKey = false,
            CollapseBoxSetItems = false,
            // Skip filter
            IsDeadPerson = true,
        };

        var sources = library.GetItemList(query);
        foreach (var alt in sources)
        {
            log.LogInformation("Deleting {Name} ({Id})", alt.Name, alt.Id);
            library.DeleteItem(alt, new DeleteOptions { DeleteFileLocation = true }, true);
        }
    }
}
