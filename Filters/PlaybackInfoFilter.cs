using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Gelato.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Gelato.Filters;

/// <summary>
/// Captures media source id for playback request and save it for later reuse.
/// Looks for both "MediaSourceId" and "RouteMediaSourceId", stores as "MediaSourceId".
/// </summary>
public sealed class PlaybackInfoFilter : IAsyncActionFilter, IOrderedFilter
{
    public int Order { get; set; } = 3;

    private const string ItemsKey = "MediaSourceId";
    private static readonly string[] InputKeys = new[] { "MediaSourceId", "RouteMediaSourceId" };

    private readonly ILibraryManager _library;
    private readonly GelatoManager _manager;
    private readonly IMediaSourceManager _sources;

    public PlaybackInfoFilter(
        ILibraryManager library,
        IMediaSourceManager sources,
        GelatoManager manager
    )
    {
        _library = library;
        _sources = sources;
        _manager = manager;
    }

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        if (ctx.GetActionName() == "GetItemSegments" && ctx.TryGetRouteGuid(out var guid))
        {
            var item = _library.GetItemById<Video>(guid);
            if (item is not null && _manager.IsGelato(item) && _manager.IsPrimaryVersion(item))
            {
                var mediaSources = _sources.GetStaticMediaSources(item, false);
                var sourceId = mediaSources[0].Id;

                if (Guid.TryParse(sourceId, out var sourceGuid))
                {
                    ctx.ReplaceGuid(sourceGuid);
                }
            }
        }
        if (ctx.ActionDescriptor is ControllerActionDescriptor cad)
            ctx.HttpContext.Items["actionName"] = cad.ActionName;

        if (ctx.HttpContext.Items.ContainsKey(ItemsKey))
        {
            await next();
            return;
        }

        if (
            TryFromArgs(ctx.ActionArguments, out var id)
            || TryFromRoute(ctx, out id)
            || TryFromQuery(ctx.HttpContext.Request, out id)
        )
        {
            if (!string.IsNullOrWhiteSpace(id))
                ctx.HttpContext.Items[ItemsKey] = id!;
        }

        await next();
    }

    private static bool TryFromArgs(IDictionary<string, object?> args, out string? id)
    {
        foreach (var kv in args)
        {
            if (kv.Value is null)
                continue;

            foreach (var key in InputKeys)
            {
                if (
                    kv.Key.Equals(key, System.StringComparison.OrdinalIgnoreCase)
                    && kv.Value is string s
                    && !string.IsNullOrWhiteSpace(s)
                )
                {
                    id = s;
                    return true;
                }
            }
        }

        foreach (var kv in args)
        {
            var v = kv.Value;
            if (v is null)
                continue;

            var type = v.GetType();
            foreach (var key in InputKeys)
            {
                var prop = type.GetProperty(
                    key,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase
                );
                if (prop?.GetValue(v) is string s && !string.IsNullOrWhiteSpace(s))
                {
                    id = s;
                    return true;
                }
            }
        }

        id = null;
        return false;
    }

    private static bool TryFromRoute(ActionExecutingContext ctx, out string? id)
    {
        foreach (var key in InputKeys)
        {
            if (
                ctx.RouteData.Values.TryGetValue(key, out var val)
                && val is string s
                && !string.IsNullOrWhiteSpace(s)
            )
            {
                id = s;
                return true;
            }
        }

        id = null;
        return false;
    }

    private static bool TryFromQuery(HttpRequest req, out string? id)
    {
        foreach (var key in InputKeys)
        {
            var val = req.Query[key];
            if (!string.IsNullOrWhiteSpace(val))
            {
                id = val.ToString();
                return true;
            }
        }

        id = null;
        return false;
    }
}
