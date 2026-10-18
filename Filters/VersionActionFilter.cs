using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

/// <summary>
/// After Jellyfin's split versions or merge versions, the affected movies/episodes sync their
/// streams again on the next visit.
/// </summary>
/// <remarks>
/// A split clears every row's owner and the links, a merge makes one item a version of another.
/// The stream sync links the rows back, but it runs once per <c>StreamTTL</c>, so a movie split
/// right after a visit showed no streams for up to an hour.
/// </remarks>
public sealed class VersionActionFilter(
    ILibraryManager libraryManager,
    GelatoManager manager,
    ILogger<VersionActionFilter> log
) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        var action = ctx.GetActionName();
        if (action is not ("DeleteAlternateSources" or "MergeVersions"))
        {
            await next();
            return;
        }

        // Resolved before the action: a split clears the owner a version page's id points at.
        var affected = new HashSet<Guid>();
        if (action == "DeleteAlternateSources" && ctx.TryGetRouteGuid(out var itemId))
        {
            Add(itemId);
        }
        else if (
            ctx.ActionArguments.TryGetValue("ids", out var arg) && arg is IEnumerable<Guid> ids
        )
        {
            foreach (var id in ids)
                Add(id);
        }

        var executed = await next();
        if (executed.Exception is not null && !executed.ExceptionHandled)
            return;

        foreach (var id in affected)
        {
            log.LogDebug("{Action}: streams of {Id} sync again on the next visit", action, id);
            manager.ResetStreamSync(id);
        }

        void Add(Guid id)
        {
            if (libraryManager.GetItemById(id) is Video video)
                affected.Add(video.PrimaryVersionId ?? video.Id);
        }
    }
}
