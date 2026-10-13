using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

/// <summary>
/// Before Jellyfin renames a library or takes a folder out of one: moves Gelato's movies and
/// series out of it (<see cref="GelatoManager.MoveOutOf"/>). Before it deletes a library:
/// deletes them, as the request asks.
/// </summary>
/// <remarks>
/// Jellyfin deletes the folder with everything in it, the items' image folders first.
/// Renaming a library does the same: the library gets a new id, and the old one goes with
/// its content. Here nothing is gone yet. A folder Jellyfin drops in a scan, without a
/// request, is caught when its deletion reaches the database
/// (<see cref="GelatoManager.RescueFromRemovedFolders"/>), which is why a deleted library's
/// items are deleted here: its folder goes in the scan after, and would be taken for one.
/// </remarks>
public sealed class LibraryStructureFilter(
    ILibraryManager library,
    GelatoManager manager,
    ILogger<LibraryStructureFilter> log
) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        var moved = false;
        try
        {
            moved = MoveOut(ctx);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not move Gelato's items out of a library that is changed");
        }

        var executed = await next();

        // Refused or failed: the folder is where it was, and takes its items back.
        if (
            moved
            && (
                executed.Exception is not null
                || executed.Result is IStatusCodeActionResult { StatusCode: >= 400 }
            )
        )
        {
            manager.AdoptParkedSoon();
        }
    }

    private bool MoveOut(ActionExecutingContext ctx)
    {
        if (
            ctx.ActionDescriptor
            is not ControllerActionDescriptor { ControllerName: "LibraryStructure" } action
        )
        {
            return false;
        }

        var delete = action.ActionName == "RemoveVirtualFolder";
        BaseItem? leaving = action.ActionName switch
        {
            "RemoveVirtualFolder" or "RenameVirtualFolder" => Library(Argument(ctx, "name")),
            "RemoveMediaPath" => manager.FindLibraryFolder(Argument(ctx, "path")),
            _ => null,
        };

        // Deleted items have nothing to come back to when the request is refused.
        return leaving is not null && manager.MoveOutOf([leaving], delete).Count > 0 && !delete;
    }

    private static string Argument(ActionExecutingContext ctx, string name) =>
        ctx.ActionArguments.TryGetValue(name, out var value) ? value as string ?? "" : "";

    private BaseItem? Library(string name) =>
        library
            .GetVirtualFolders()
            .FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase))
            is { ItemId: { } id }
        && Guid.TryParse(id, out var guid)
            ? library.GetItemById(guid)
            : null;
}
