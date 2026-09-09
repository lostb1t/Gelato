using Gelato;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using Xunit;

namespace Gelato.Tests;

public sealed class ActionContextExtensionsTests
{
    private static readonly Guid UserId = Guid.Parse("a369188f-acbe-40c4-8d86-5b2debc3a8bf");
    private static readonly Guid ItemId = Guid.Parse("31dae9f1-c0a6-5a22-d768-32c87e057299");

    [Fact]
    public void UserScopedDetailRouteIsInsertableAndResolvesRouteUserAndItem()
    {
        var context = CreateContext("GetItemLegacy", new Dictionary<string, object?>
        {
            ["userId"] = UserId,
            ["itemId"] = ItemId,
        });

        Assert.True(context.HttpContext.IsInsertableAction());
        Assert.True(context.TryGetRouteGuid(out var item));
        Assert.Equal(ItemId, item);
        Assert.True(context.TryGetUserId(out var user));
        Assert.Equal(UserId, user);
    }

    [Fact]
    public void NonGelatoGuidPassesThroughWithoutChangingArguments()
    {
        var context = CreateContext("GetItemLegacy", new Dictionary<string, object?>
        {
            ["userId"] = UserId,
            ["itemId"] = ItemId,
        });

        Assert.True(context.IsInsertableAction());
        Assert.True(context.TryGetRouteGuid(out var original));
        context.ReplaceGuid(ItemId);
        Assert.Equal(original, (Guid)context.ActionArguments["itemId"]!);
    }

    [Fact]
    public void ReplaceGuidUpdatesItemRouteAndActionArgumentButNotUserRoute()
    {
        var context = CreateContext("GetItemLegacy", new Dictionary<string, object?>
        {
            ["userId"] = UserId,
            ["itemId"] = ItemId,
        });
        var canonical = Guid.Parse("b070ee5c-57c4-6e88-b1e7-046f1949cf57");

        context.ReplaceGuid(canonical);

        Assert.Equal(canonical.ToString(), context.RouteData.Values["itemId"]);
        Assert.Equal(canonical, context.ActionArguments["itemId"]);
        Assert.Equal(UserId, context.RouteData.Values["userId"]);
        Assert.Equal(UserId, context.ActionArguments["userId"]);
    }

    [Fact]
    public void MissingOrWrongUserRouteDoesNotResolveAUser()
    {
        var missing = CreateContext("GetItemLegacy", new Dictionary<string, object?>
        {
            ["itemId"] = ItemId,
        });
        var wrong = CreateContext("GetItemLegacy", new Dictionary<string, object?>
        {
            ["userId"] = "not-a-guid",
            ["itemId"] = ItemId,
        });

        Assert.False(missing.TryGetUserId(out _));
        Assert.False(wrong.TryGetUserId(out _));
    }

    private static ActionExecutingContext CreateContext(
        string actionName,
        Dictionary<string, object?> routeValues
    )
    {
        var method = typeof(ActionContextExtensionsTests).GetMethod(
            nameof(CreateContext),
            BindingFlags.Static | BindingFlags.NonPublic
        )!;
        var descriptor = new ControllerActionDescriptor
        {
            ActionName = actionName,
            ControllerName = "UserLibrary",
            MethodInfo = method,
        };
        var http = new DefaultHttpContext();
        http.SetEndpoint(new Microsoft.AspNetCore.Http.Endpoint(
            _ => Task.CompletedTask,
            new Microsoft.AspNetCore.Http.EndpointMetadataCollection(descriptor),
            "test"
        ));
        var routeData = new Microsoft.AspNetCore.Routing.RouteData();
        foreach (var pair in routeValues)
            routeData.Values[pair.Key] = pair.Value;
        var action = new ActionContext(http, routeData, descriptor);
        return new ActionExecutingContext(action, [], new Dictionary<string, object?>(routeValues), new object());
    }
}
