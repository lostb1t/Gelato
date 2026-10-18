using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

/// <summary>
/// Answers a movie/episode's segments with those of the stream that plays under its id.
/// </summary>
/// <remarks>
/// The segment providers run for the stream row being played, so its segments (an IntroDB intro)
/// are stored under the row. The first stream is listed with the movie's id, so its watch state
/// stays on the movie, and the player asks <c>/MediaSegments/{movie id}</c> for it: there are
/// none, and the skip button never showed. A version picked from the dropdown plays under its
/// row's id and needs nothing.
/// </remarks>
public sealed class MediaSegmentFilter(
    ILibraryManager libraryManager,
    IMediaSourceManager mediaSourceManager,
    IMediaSegmentManager mediaSegmentManager,
    IUserManager userManager,
    ILogger<MediaSegmentFilter> log
) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        if (ctx.GetActionName() == "GetItemSegments" && ctx.TryGetRouteGuid(out var itemId))
        {
            try
            {
                if (PlayingRow(ctx, itemId) is { } rowId)
                {
                    log.LogDebug("Segments of {ItemId} are those of its stream {RowId}", itemId, rowId);
                    ctx.ActionArguments["itemId"] = rowId;
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Could not find the stream that plays as {ItemId}", itemId);
            }
        }

        await next();
    }

    /// <summary>
    /// The stream row listed with the movie/episode's id for this user, when it has segments.
    /// The movie's own are kept otherwise.
    /// </summary>
    private Guid? PlayingRow(ActionExecutingContext ctx, Guid itemId)
    {
        if (
            libraryManager.GetItemById(itemId) is not Video video
            || !video.IsPrimaryVersion()
            || !video.IsGelatoPlaybackItem()
            || !ctx.TryGetUserId(out var userId)
            || userManager.GetUserById(userId) is not { } user
        )
        {
            return null;
        }

        var id = itemId.ToString("N", CultureInfo.InvariantCulture);
        var source = mediaSourceManager
            .GetStaticMediaSources(video, false, user)
            .FirstOrDefault(s => s.Id == id);

        // Gelato's sources name their row in the ETag.
        return
            Guid.TryParse(source?.ETag, out var rowId)
            && rowId != itemId
            && mediaSegmentManager.HasSegments(rowId)
            ? rowId
            : null;
    }
}
