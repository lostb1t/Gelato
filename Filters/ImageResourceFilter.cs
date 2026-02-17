using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

/// <summary>
///  Replaces image requests from stremio sources
/// </summary>
public sealed class ImageResourceFilter : IAsyncResourceFilter {
    private readonly ILibraryManager _library;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ImageResourceFilter> _log;
    private readonly GelatoManager _manager;

    public ImageResourceFilter(
        ILibraryManager library,
        IHttpClientFactory http,
        GelatoManager manager,
        ILogger<ImageResourceFilter> log
    ) {
        _library = library;
        _http = http;
        _log = log;
        _manager = manager;
    }

    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext ctx,
        ResourceExecutionDelegate next
    ) {
        if (
            ctx.ActionDescriptor is not ControllerActionDescriptor cad
            || cad.ActionName != "GetItemImage"
        ) {
            await next();
            return;
        }

        var routeValues = ctx.RouteData.Values;

        if (
            !routeValues.TryGetValue("itemId", out var guidString)
            || !Guid.TryParse(guidString?.ToString(), out var guid)
        ) {
            await next();
            return;
        }

        var stremioMeta = _manager.GetStremioMeta(guid);
        if (stremioMeta?.Poster is null) {
            await next();
            return;
        }

        var url = stremioMeta.Poster;

        try {
            var client = _http.CreateClient();
            using var res = await client.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                ctx.HttpContext.RequestAborted
            );

            if (!res.IsSuccessStatusCode) {
                await next();
                return;
            }

            var contentType = res.Content.Headers.ContentType?.ToString() ?? "image/jpeg";
            ctx.HttpContext.Response.ContentType = contentType;

            await using var responseStream = await res.Content.ReadAsStreamAsync(
                ctx.HttpContext.RequestAborted
            );
            await responseStream.CopyToAsync(
                ctx.HttpContext.Response.Body,
                ctx.HttpContext.RequestAborted
            );

            return;
        }
        catch (Exception ex) {
            _log.LogWarning(ex, "Image proxy failed for item {ItemId}", guid);
            await next();
        }
    }
}
