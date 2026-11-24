using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

/// <summary>
/// An episode's queue starts with the version the client named: <c>GET /Shows/{id}/Episodes?startItemId=</c>
/// with a stream row of an episode lists that row where Jellyfin lists the episode.
/// </summary>
/// <remarks>
/// Work-around for jellyfin-androidtv#5931. With "queue next episodes" on (the default), Android TV
/// plays an episode through that list: picking a version opens the row, but Play asks for the
/// episodes starting at it and plays the first item. Jellyfin starts from the row's primary
/// episode instead (a row is no episode of the listing), so the pick was replaced by the default
/// stream. Upstream expects the server to answer with the version asked for.
/// </remarks>
public sealed class EpisodeStartFilter(
    ILibraryManager libraryManager,
    IUserManager userManager,
    IDtoService dtoService,
    ILogger<EpisodeStartFilter> log
) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        if (
            ctx.GetActionName() is not "GetEpisodes"
            || !ctx.TryGetActionArgument<Guid?>("startItemId", out var startId)
            || startId is not { } rowId
            || libraryManager.GetItemById(rowId) is not Video { PrimaryVersionId: { } primaryId } row
            || !row.IsStreamRow()
        )
        {
            await next();
            return;
        }

        var executed = await next();
        if (
            executed.Exception is not null
            || executed.Result is not ObjectResult { Value: QueryResult<BaseItemDto> list }
            || list.Items is not { Count: > 0 } items
            || items.FirstOrDefault(i => i.Id == primaryId) is not { } episode
        )
            return;

        ctx.TryGetUserId(out var userId);
        ctx.TryGetActionArgument<ItemFields[]>("fields", out var fields, []);
        ctx.TryGetActionArgument<bool?>("enableUserData", out var enableUserData);
        var options = new DtoOptions { Fields = fields, EnableUserData = enableUserData ?? true };
        var user = userId == Guid.Empty ? null : userManager.GetUserById(userId);

        var dto = dtoService.GetBaseItemDto(row, options, user);
        executed.Result = new OkObjectResult(
            new QueryResult<BaseItemDto>(
                list.StartIndex,
                list.TotalRecordCount,
                items.Select(i => i.Id == primaryId ? dto : i).ToArray()
            )
        );
        log.LogDebug(
            "GetEpisodes startItemId={RowId}: lists the version in place of {EpisodeId}",
            rowId,
            episode.Id
        );
    }
}
