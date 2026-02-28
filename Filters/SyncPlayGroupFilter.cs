using System;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

/// <summary>
/// Intercepts SyncPlay create/join/leave controller actions to maintain
/// <see cref="SyncPlayGroupTracker"/>'s userId → groupId mapping.
/// </summary>
public sealed class SyncPlayGroupFilter(ILogger<SyncPlayGroupFilter> log) : IAsyncActionFilter, IOrderedFilter
{
    // Run before Gelato's other filters so the mapping is ready
    // when playback requests arrive.
    public int Order { get; init; } = -1;

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next)
    {
        var actionName = (ctx.ActionDescriptor as ControllerActionDescriptor)?.ActionName;

        if (actionName is not (
            "SyncPlayCreateGroup" or
            "SyncPlayJoinGroup" or
            "SyncPlayLeaveGroup"))
        {
            await next();
            return;
        }

        log.LogDebug("SyncPlayGroupFilter: intercepted action={Action}", actionName);

        // Execute the actual controller action first.
        var executed = await next();

        // Only update our map when the action succeeded.
        if (executed.Exception is not null || executed.Canceled)
        {
            log.LogDebug("SyncPlayGroupFilter: action failed or canceled");
            return;
        }

        if (!ctx.HttpContext.TryGetUserId(out var userId))
        {
            log.LogDebug("SyncPlayGroupFilter: could not get userId from context");
            return;
        }

        switch (actionName)
        {
            case "SyncPlayCreateGroup":
                // NewGroup returns Ok(GroupInfoDto) — extract GroupId from the result.
                if (executed.Result is ObjectResult { Value: { } resultObj })
                {
                    log.LogInformation("SyncPlayGroupFilter: CreateGroup result type={Type}", resultObj.GetType().Name);
                    var groupIdProp = resultObj.GetType()
                        .GetProperty("GroupId", BindingFlags.Public | BindingFlags.Instance);
                    if (groupIdProp?.GetValue(resultObj) is Guid createdGroupId)
                    {
                        SyncPlayGroupTracker.SetGroup(userId, createdGroupId);
                        log.LogInformation("SyncPlayGroupFilter: TRACKED create user={UserId} group={GroupId}", userId, createdGroupId);
                    }
                    else
                    {
                        log.LogWarning("SyncPlayGroupFilter: could not extract GroupId from result");
                    }
                }
                else
                {
                    log.LogWarning("SyncPlayGroupFilter: CreateGroup result was not ObjectResult, was {Type}", executed.Result?.GetType().Name ?? "null");
                }
                break;

            case "SyncPlayJoinGroup":
                // JoinGroup request body contains GroupId.
                if (TryGetGroupIdFromArgs(ctx, out var joinedGroupId))
                {
                    SyncPlayGroupTracker.SetGroup(userId, joinedGroupId);
                    log.LogInformation("SyncPlayGroupFilter: TRACKED join user={UserId} group={GroupId}", userId, joinedGroupId);
                }
                else
                {
                    log.LogWarning("SyncPlayGroupFilter: could not extract GroupId from join args");
                }
                break;

            case "SyncPlayLeaveGroup":
                SyncPlayGroupTracker.RemoveUser(userId);
                log.LogDebug("SyncPlayGroupFilter: TRACKED leave user={UserId}", userId);
                break;
        }
    }

    private static bool TryGetGroupIdFromArgs(
        ActionExecutingContext ctx, out Guid groupId)
    {
        groupId = Guid.Empty;

        // Look for requestData argument with a GroupId property.
        foreach (var kv in ctx.ActionArguments)
        {
            if (kv.Value is null) continue;

            var prop = kv.Value.GetType()
                .GetProperty("GroupId", BindingFlags.Public | BindingFlags.Instance);
            if (prop?.GetValue(kv.Value) is Guid id && id != Guid.Empty)
            {
                groupId = id;
                return true;
            }
        }

        return false;
    }
}
