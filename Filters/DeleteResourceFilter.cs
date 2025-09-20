
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
// using Jellyfin.Api.Controllers;
namespace Gelato.Filters;


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

        // var principal = ctx.HttpContext.User;
        // userIdObj ??= ctx.HttpContext.User.Claims.FirstOrDefault(claim => claim.Type.Equals("Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;

        //    var user = userIdObj switch
        //   {
        //      string strUserId => _userManager.GetUserById(Guid.Parse(strUserId)),
        //     Guid guidUserId => _userManager.GetUserById(guidUserId),
        //     _ => null
        // };
        // _log.LogInformation($" DeleteResourceFilter invoked by {principal.Identity?.Name}");
        // var claimsPrincipal = ctx.HttpContext.User;
        // var userIdStr = claimsPrincipal.Claims.FirstOrDefault(c => c.Type == "UserId")?.Value;
        // _log.LogInformation($"[Gelato] UserId claim: {userIdStr}");

        // if (Guid.TryParse(userIdStr, out var userId))
        // {
        //     var user = _userManager.GetUserById(userId);
        //     Console.WriteLine($"[Gelato] Current user: {user.Username}");
        // }

        // var userId = principal.GetUserId();
        // var isApiKey = User.GetIsApiKey();
        // var user = userId.IsEmpty() && isApiKey
        //     ? null
        //     : _userManager.GetUserById(userId);

        // if (user is null && !isApiKey)
        // {
        //     return NotFound();
        // }

        // var item = _libraryManager.GetItemById<BaseItem>(itemId, user);
        // if (item is null)
        // {
        //     //return base.DeleteItem(itemId);
        // }
        await next();
        return;
    }

}