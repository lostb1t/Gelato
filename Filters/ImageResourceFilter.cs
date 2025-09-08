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
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace Gelato.Filters;

public sealed class ImageResourceFilter : IAsyncResourceFilter
{
    private readonly ILibraryManager _library;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<ImageResourceFilter> _log;
    private readonly GelatoManager _manager;

    public ImageResourceFilter(
        ILibraryManager library,
        IHttpClientFactory http,
        GelatoManager manager,
        ILogger<ImageResourceFilter> log)
    {
        _library = library;
        _http = http;
        _log = log;
         _manager = manager;
    }

    public async Task OnResourceExecutionAsync(ResourceExecutingContext ctx, ResourceExecutionDelegate next)
    {
        var http = ctx.HttpContext;
        var req = http.Request;

       if (ctx.ActionDescriptor is not ControllerActionDescriptor cad
    || cad.ActionName != "GetItemImage")
{
    await next();
    return;
}
        
        
        var routeValues = ctx.RouteData.Values;

        if (!routeValues.TryGetValue("itemId", out var guidString) ||
    !Guid.TryParse(guidString?.ToString(), out var guid))
{
           await next();
            return;
}


        
        var stremioMeta = _manager.GetStremioMeta(guid);
        if (stremioMeta is null)
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
        ctx.HttpContext.Response.Redirect(stremioMeta.Poster, permanent: false);
    }

}