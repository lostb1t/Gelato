
using System.Net.Http;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using MediaBrowser.Common.Extensions;
using Microsoft.Extensions.Logging;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using Microsoft.AspNetCore.Mvc;
// using Jellyfin.Api.Controllers;
namespace Gelato.Filters;

// using Microsoft.AspNetCore.Mvc.Filters;


public sealed class DeleteResourceFilter : IAsyncActionFilter
{
    private readonly ILibraryManager _library;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ImageResourceFilter> _log;
    private readonly GelatoManager _manager;
    private readonly IUserManager _userManager;

    public DeleteResourceFilter(
        ILibraryManager library,
        IHttpClientFactory http,
        GelatoManager manager,
        IUserManager userManager,
        ILogger<ImageResourceFilter> log)
    {
        _library = library;
        _http = http;
        _log = log;
        _manager = manager;
        _userManager = userManager;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {

        if (ctx.ActionDescriptor is not ControllerActionDescriptor cad
     || cad.ActionName != "DeleteItem")
        {
            await next();
            return;
        }

        if (!_manager.TryGetRouteGuid(ctx, out var guid))
        {
            await next();
            return;
        }

        var principal = ctx.HttpContext.User;
        var userIdStr = ctx.HttpContext.User.Claims.FirstOrDefault(c => c.Type == "UserId" || c.Type == "Jellyfin-UserId")?.Value;
        _log.LogInformation($"[Gelato] UserId claim: {userIdStr}");

        User? user = null;
        if (Guid.TryParse(userIdStr, out var userId))
        {
            user = _userManager.GetUserById(userId);
            Console.WriteLine($"[Gelato] Current user: {user.Username}");
        }

        if (user is not null)
        {
            var item = _library.GetItemById<BaseItem>(guid, user);
            if (item is not null)
            {
                if (_manager.IsStremio(item))
                {
                    if (_manager.CanDelete(item, user))
                    {
                        _log.LogInformation($"[Gelato] Deleting {item.Name}");
                        _library.DeleteItem(
                            item,
                            new DeleteOptions { DeleteFileLocation = false },
                            true);

                        ctx.Result = new NoContentResult();
                        return;
                    }
                }
            }
        }

        await next();
        return;
    }

}