
using System.Net.Http;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

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

        if (!_manager.TryGetRouteGuid(ctx, out var guid))
        {
            await next();
            return;
        }

        var principal = ctx.HttpContext.User;
        var userIdStr = ctx.HttpContext.User.Claims.FirstOrDefault(c => c.Type == "UserId" || c.Type == "Jellyfin-UserId")?.Value;

        User? user = null;
        if (Guid.TryParse(userIdStr, out var userId))
        {
            user = _userManager.GetUserById(userId);
        }

        if (user is not null)
        {
            var item = _library.GetItemById<BaseItem>(guid, user);
            if (item is not null)
            {
                if (_manager.IsGelato(item))
                {
                    if (_manager.CanDelete(item, user))
                    {
                        if (item is Video video && _manager.IsPrimaryVersion(video))
                        {
                            foreach (var link in video.LinkedAlternateVersions) // link : LinkedChild
                            {
                                if (link.ItemId is not Guid altId || altId == Guid.Empty)
                                {
                                    _log.LogWarning("ALT LINK: missing/empty ItemId; skipping. {@Link}", link);
                                    continue;
                                }

                                var alt = _library.GetItemById(altId);
                                if (alt is null)
                                {
                                    _log.LogWarning("ALT LINK: item not found in DB: {AltId}", altId);
                                    continue;
                                }

                                _log.LogInformation("Deleting alternate version {Name} ({Id})", alt.Name, alt.Id);

                                _library.DeleteItem(
                                    alt,
                                    new DeleteOptions { DeleteFileLocation = false },
                                    true
                                );
                            }
                        }

                        _log.LogInformation($"deleting {item.Name}");
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
