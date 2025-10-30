using System.Net.Http;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
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
    private readonly ILogger<DeleteResourceFilter> _log;
    private readonly GelatoManager _manager;
    private readonly IUserManager _userManager;

    public DeleteResourceFilter(
        ILibraryManager library,
        IHttpClientFactory http,
        GelatoManager manager,
        IUserManager userManager,
        ILogger<DeleteResourceFilter> log
    )
    {
        _library = library;
        _http = http;
        _log = log;
        _manager = manager;
        _userManager = userManager;
    }

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        if (
            ctx.ActionDescriptor is not ControllerActionDescriptor cad
            || cad.ActionName != "DeleteItem"
        )
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
        var userIdStr = ctx
            .HttpContext.User.Claims.FirstOrDefault(c =>
                c.Type == "UserId" || c.Type == "Jellyfin-UserId"
            )
            ?.Value;

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
                            var query = new InternalItemsQuery
                            {
                                IncludeItemTypes = new[] { item.GetBaseItemKind() },
                                HasAnyProviderId = new()
                                {
                                    { "Stremio", item.GetProviderId("Stremio") },
                                },
                                Recursive = false,
                                GroupByPresentationUniqueKey = false,
                                GroupBySeriesPresentationUniqueKey = false,
                                CollapseBoxSetItems = false,
                            };

                            var sources = _library.GetItemList(query).OfType<Video>().ToList();
                            foreach (var alt in sources)
                            {
                                _log.LogInformation(
                                    "Deleting alternate version {Name} ({Id})",
                                    alt.Name,
                                    alt.Id
                                );

                                _library.DeleteItem(
                                    alt,
                                    new DeleteOptions { DeleteFileLocation = false },
                                    true
                                );
                            }
                        }
                        else
                        {
                            _log.LogInformation($"deleting {item.Name}");
                            _library.DeleteItem(
                                item,
                                new DeleteOptions { DeleteFileLocation = false },
                                true
                            );
                        }

                        ctx.Result = new NoContentResult();
                        return;
                    }
                }
            }
        }

        await next();
    }
}
