using System;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Gelato.Filters;

/// <summary>
/// Intercepts SyncPlay create/join/leave controller actions to maintain
/// <see cref="SyncPlayGroupTracker"/>'s userId → groupId mapping.
/// </summary>
public sealed class SyncPlayGroupFilter : IAsyncActionFilter, IOrderedFilter
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

        // Execute the actual controller action first.
        var executed = await next();

        // Only update our map when the action succeeded.
        if (executed.Exception is not null || executed.Canceled)
            return;

        if (!ctx.HttpContext.TryGetUserId(out var userId))
            return;

        switch (actionName)
        {
            case "SyncPlayCreateGroup":
                // NewGroup returns Ok(GroupInfoDto) — extract GroupId from the result.
                if (executed.Result is ObjectResult { Value: { } resultObj })
                {
                    var groupIdProp = resultObj.GetType()
                        .GetProperty("GroupId", BindingFlags.Public | BindingFlags.Instance);
                    if (groupIdProp?.GetValue(resultObj) is Guid createdGroupId)
                    {
                        SyncPlayGroupTracker.SetGroup(userId, createdGroupId);
                    }
                }
                break;

            case "SyncPlayJoinGroup":
                // JoinGroup request body contains GroupId.
                if (TryGetGroupIdFromArgs(ctx, out var joinedGroupId))
                {
                    SyncPlayGroupTracker.SetGroup(userId, joinedGroupId);
                }
                break;

            case "SyncPlayLeaveGroup":
                SyncPlayGroupTracker.RemoveUser(userId);
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
