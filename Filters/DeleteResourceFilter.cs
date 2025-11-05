using System;
using System.Linq;
using System.Threading.Tasks;
using Gelato.Common;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

public sealed class DeleteResourceFilter : IAsyncActionFilter
{
    private readonly ILibraryManager _library;
    private readonly ILogger<DeleteResourceFilter> _log;
    private readonly GelatoManager _manager;
    private readonly IUserManager _userManager;

    public DeleteResourceFilter(
        ILibraryManager library,
        GelatoManager manager,
        IUserManager userManager,
        ILogger<DeleteResourceFilter> log
    )
    {
        _library = library;
        _log = log;
        _manager = manager;
        _userManager = userManager;
    }

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        // Only intercept DeleteItem actions with valid user
        if (
            ctx.GetActionName() != "DeleteItem"
            || !ctx.TryGetRouteGuid(out var guid)
            || !ctx.TryGetUserId(out var userId)
            || _userManager.GetUserById(userId) is not User user
        )
        {
            await next();
            return;
        }

        var item = _library.GetItemById<BaseItem>(guid, user);

        // Only handle Gelato items that user can delete
        if (item is null || !_manager.IsGelato(item) || !_manager.CanDelete(item, user))
        {
            await next();
            return;
        }

        // Handle deletion and return 204 No Content
        DeleteItem(item);
        ctx.Result = new NoContentResult();
    }

    private void DeleteItem(BaseItem item)
    {
        if (item is Video video && _manager.IsPrimaryVersion(video))
        {
            DeleteStreams(video);
        }
        else
        {
            _log.LogInformation("Deleting {Name}", item.Name);
            _library.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, true);
        }
    }

    private void DeleteStreams(Video video)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { video.GetBaseItemKind() },
            HasAnyProviderId = new() { { "Stremio", video.ProviderIds["Stremio"] } },
            Recursive = false,
            GroupByPresentationUniqueKey = false,
            GroupBySeriesPresentationUniqueKey = false,
            CollapseBoxSetItems = false,
            // Skip filter
            IsDeadPerson = true,
        };

        var sources = _library.GetItemList(query).OfType<Video>();
        foreach (var alt in sources)
        {
            _log.LogInformation("Deleting {Name} ({Id})", alt.Name, alt.Id);
            _library.DeleteItem(alt, new DeleteOptions { DeleteFileLocation = true }, true);
        }
    }
}
