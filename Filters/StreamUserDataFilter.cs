using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

/// <summary>
/// Applies a favourite, rating or user data change made on a stream row to its movie/episode too.
/// </summary>
/// <remarks>
/// Every version shows its movie's user data (DtoServiceDecorator), so a change set on a version
/// page belongs on the movie. The saved event cannot be used for this: it carries the row's whole
/// stored state, and everything but the field that was set is stale there. So the request itself
/// is repeated for the movie, with only the fields it sets.
/// </remarks>
public sealed class StreamUserDataFilter(
    ILibraryManager libraryManager,
    IUserManager userManager,
    IUserDataManager userDataManager,
    ILogger<StreamUserDataFilter> log
) : IAsyncActionFilter
{
    private static readonly HashSet<string> Actions =
    [
        "MarkFavoriteItem",
        "MarkFavoriteItemLegacy",
        "UnmarkFavoriteItem",
        "UnmarkFavoriteItemLegacy",
        "UpdateUserItemRating",
        "UpdateUserItemRatingLegacy",
        "DeleteUserItemRating",
        "DeleteUserItemRatingLegacy",
        "UpdateItemUserData",
        "UpdateItemUserDataLegacy",
    ];

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        if (ctx.GetActionName() is not { } action || !Actions.Contains(action))
        {
            await next();
            return;
        }

        var executed = await next();
        if (executed.Exception is not null && !executed.ExceptionHandled)
            return;

        try
        {
            if (
                !ctx.TryGetRouteGuid(out var itemId)
                || libraryManager.GetItemById(itemId)
                    is not Video { PrimaryVersionId: { } primaryId } row
                || !row.HasStreamTag()
                || libraryManager.GetItemById(primaryId) is not { } primary
                || GetUser(ctx) is not { } user
                || userDataManager.GetUserData(user, primary) is not { } data
            )
            {
                return;
            }

            switch (action)
            {
                case "MarkFavoriteItem" or "MarkFavoriteItemLegacy":
                    data.IsFavorite = true;
                    Save(UserDataSaveReason.UpdateUserRating);
                    break;
                case "UnmarkFavoriteItem" or "UnmarkFavoriteItemLegacy":
                    data.IsFavorite = false;
                    Save(UserDataSaveReason.UpdateUserRating);
                    break;
                case "UpdateUserItemRating" or "UpdateUserItemRatingLegacy":
                    data.Likes =
                        ctx.ActionArguments.TryGetValue("likes", out var likes)
                        && likes is bool liked
                            ? liked
                            : null;
                    Save(UserDataSaveReason.UpdateUserRating);
                    break;
                case "DeleteUserItemRating" or "DeleteUserItemRatingLegacy":
                    data.Likes = null;
                    Save(UserDataSaveReason.UpdateUserRating);
                    break;
                default:
                    if (ctx.TryGetActionArgument<UpdateUserItemDataDto>("userDataDto", out var dto))
                    {
                        userDataManager.SaveUserData(
                            user,
                            primary,
                            dto,
                            UserDataSaveReason.UpdateUserData
                        );
                    }
                    break;
            }

            void Save(UserDataSaveReason reason) =>
                userDataManager.SaveUserData(user, primary, data, reason, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The request itself succeeded; never fail it over the movie's copy.
            log.LogWarning(ex, "Could not apply {Action} of a stream to its movie", action);
        }
    }

    private Jellyfin.Database.Implementations.Entities.User? GetUser(ActionExecutingContext ctx)
    {
        // The id the action ran for: an administrator may act for another user.
        if (
            ctx.ActionArguments.TryGetValue("userId", out var arg)
            && arg is Guid id
            && id != Guid.Empty
        )
        {
            return userManager.GetUserById(id);
        }

        return ctx.TryGetUserId(out var userId) ? userManager.GetUserById(userId) : null;
    }
}
