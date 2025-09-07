using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ExternalMedia;

public sealed class ExternalMediaImageResourceFilter : IAsyncResourceFilter
{
    private readonly ILibraryManager _library;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ExternalMediaImageResourceFilter> _log;

    public ExternalMediaImageResourceFilter(
        ILibraryManager library,
        IHttpClientFactory http,
        ILogger<ExternalMediaImageResourceFilter> log)
    {
        _library = library;
        _http = http;
        _log = log;
    }

    public async Task OnResourceExecutionAsync(ResourceExecutingContext ctx, ResourceExecutionDelegate next)
    {
        var http = ctx.HttpContext;
        var req = http.Request;

        // Only intercept GET /Items/{id}/Images/{type}(/index?)
        if (!HttpMethods.IsGet(req.Method) || !TryParse(req.Path, out var itemId, out var type, out var index))
        {
            await next();
            return;
        }

        // Only handle items we consider "external"
        // if (!IsExternal(item))
        // {
        //     await next();
        //     return;
        // }

        // Ask resolver for an override URL
        //if (!_resolver.TryResolve(item, type, index, out var url) || string.IsNullOrWhiteSpace(url))
        //{
        //    await next();
        //    return;
        //}

        //_log.LogDebug("Redirecting image request {Path} to {Url}", req.Path, url);
        //context.HttpContext.Response.Redirect(url, permanent: false);
    }

    private static bool TryParse(PathString path, out Guid itemId, out ImageType type, out int index)
    {
        itemId = Guid.Empty;
        type = ImageType.Primary;
        index = 0;

        // Expected: /Items/{id}/Images/{type}[/ {index}]
        var parts = path.Value?.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts is null || (parts.Length != 4 && parts.Length != 5)) return false;
        if (!parts[0].Equals("Items", StringComparison.OrdinalIgnoreCase)) return false;
        if (!parts[2].Equals("Images", StringComparison.OrdinalIgnoreCase)) return false;

        if (!Guid.TryParse(parts[1], out itemId)) return false;
        if (!Enum.TryParse(parts[3], ignoreCase: true, out type)) return false;

        if (parts.Length == 5 && !int.TryParse(parts[4], out index)) return false;
        return true;
    }

    // Heuristic: treat STRM/shortcut or a custom provider id as "external"
    private static bool IsExternal(BaseItem item)
    {
        // STRM or plugin-tagged items
        var path = item.Path ?? string.Empty;
        if (path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)) return true;

        // Optional: if you tag items with a provider id like "external"
        var pid = item.ProviderIds != null && item.ProviderIds.TryGetValue("external", out var _);
        return pid;
    }
}