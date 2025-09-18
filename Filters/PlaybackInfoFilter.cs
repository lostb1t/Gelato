using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Http.Extensions;

namespace Gelato.Filters;

/// <summary>
/// Captures media source index for playback request and ave it for later reuse.
/// </summary>
public sealed class PlaybackInfoFilter : IAsyncActionFilter, IOrderedFilter
{
    public int Order { get; set; } = 3;
    private const string Key = "MediaSourceId";

    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {

  // always save action name
        if (ctx.ActionDescriptor is ControllerActionDescriptor cad)
        {

            ctx.HttpContext.Items["actionName"] = cad.ActionName;
        }
        // Already captured on this request? bail.
        if (ctx.HttpContext.Items.ContainsKey(Key))
        {
            await next();
            return;
        }

        if (TryFromArgs(ctx.ActionArguments, out var id) ||
            TryFromQuery(ctx.HttpContext.Request, out id))
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                ctx.HttpContext.Items[Key] = id!;
                // System.Console.WriteLine($"[Gelato] Captured MediaSourceId = {id}");
            }
        }

        await next();
    }

    private static bool TryFromArgs(IDictionary<string, object?> args, out string? id)
    {
        foreach (var kv in args)
        {
            if (kv.Value is null) continue;

            if (kv.Key.Equals(Key, System.StringComparison.OrdinalIgnoreCase)
                && kv.Value is string s && !string.IsNullOrWhiteSpace(s))
            {
                id = s;
                return true;
            }
        }

        foreach (var kv in args)
        {
            var v = kv.Value;
            if (v is null) continue;

            var prop = v.GetType().GetProperty(Key, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop?.GetValue(v) is string s && !string.IsNullOrWhiteSpace(s))
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
        var val = req.Query[Key];
        if (!string.IsNullOrWhiteSpace(val))
        {
            id = val.ToString();
            return true;
        }

        id = null;
        return false;
    }
}