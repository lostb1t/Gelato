using System.Reflection;
using Gelato.Services;
using MediaBrowser.Controller.SyncPlay;
using MediaBrowser.Model.SyncPlay;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

/// <summary>
/// Records which SyncPlay group a session is in, and the version its creator was playing.
/// </summary>
/// <remarks>
/// A group created during playback starts with the creator's item, by id only: without the
/// version recorded here the creator would jump to the first one and the members would join on it.
/// </remarks>
public sealed class SyncPlayFilter(
    SyncPlaySources sources,
    ISyncPlayManager syncPlayManager,
    ILogger<SyncPlayFilter> log
) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        var action = ctx.GetActionName();
        if (
            action
            is not (
                "SyncPlayCreateGroup"
                or "SyncPlayJoinGroup"
                or "SyncPlayLeaveGroup"
                or "SyncPlaySetNewQueue"
            )
        )
        {
            await next();
            return;
        }

        // The creator's play state, before the new group restarts its playback.
        var session = sources.SessionOf(ctx.HttpContext);
        var playing = session?.NowPlayingItem?.Id;
        var playingSource = session?.PlayState?.MediaSourceId;

        var executed = await next();
        session ??= sources.SessionOf(ctx.HttpContext);
        if ((executed.Exception is not null && !executed.ExceptionHandled) || session is null)
            return;

        switch (action)
        {
            case "SyncPlayCreateGroup":
                if (executed.Result is ObjectResult { Value: GroupInfoDto group })
                {
                    sources.Joined(session.Id, group.GroupId);
                    if (
                        playing is { } itemId
                        && playingSource is not null
                        && Guid.TryParse(playingSource, out var sourceId)
                        && sourceId != itemId
                    )
                    {
                        sources.Choose(group.GroupId, itemId, playingSource);
                        log.LogDebug(
                            "SyncPlay group {GroupId} plays version {SourceId} of {ItemId}",
                            group.GroupId,
                            playingSource,
                            itemId
                        );
                    }
                }
                break;
            case "SyncPlayJoinGroup":
                // Jellyfin answers a join it refused (no such group, no access to its queue) with
                // success too: only a group that lists the user now counts.
                if (
                    GroupIdOf(ctx) is { } joined
                    && syncPlayManager.IsUserActive(session.UserId)
                    && syncPlayManager.GetGroup(session, joined)?.Participants is { } participants
                    && participants.Contains(session.UserName, StringComparer.Ordinal)
                )
                {
                    sources.Joined(session.Id, joined);
                }
                break;
            case "SyncPlayLeaveGroup":
                sources.Left(session.Id);
                break;
            case "SyncPlaySetNewQueue":
                sources.QueueSet(session.Id);
                break;
        }
    }

    // JoinGroupRequestDto lives in Jellyfin.Api, which plugins do not reference.
    private static Guid? GroupIdOf(ActionExecutingContext ctx)
    {
        foreach (var value in ctx.ActionArguments.Values)
        {
            if (
                value?.GetType()
                    .GetProperty("GroupId", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(value)
                    is Guid id
                && id != Guid.Empty
            )
            {
                return id;
            }
        }

        return null;
    }
}
