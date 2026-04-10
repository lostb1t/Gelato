using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

/// <summary>
/// Proxies image requests for search results (non-library gelato items).
/// Library item images are handled by ImageProcessorDecorator.
/// </summary>
public sealed class ImageResourceFilter(
    IHttpClientFactory http,
    GelatoManager manager,
    ILogger<ImageResourceFilter> log
) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext ctx,
        ResourceExecutionDelegate next
    )
    {
        if (
            ctx.ActionDescriptor
            is not ControllerActionDescriptor
            {
                ActionName: "GetItemImage" or "GetItemImageByIndex" or "GetItemImage2"
            }
        )
        {
            await next();
            return;
        }

        var routeValues = ctx.RouteData.Values;

        if (
            !routeValues.TryGetValue("itemId", out var guidString)
            || !Guid.TryParse(guidString?.ToString(), out var guid)
        )
        {
            await next();
            return;
        }

        // Only handle cached search results — library items go through ProcessImage
        var url = manager.GetStremioMeta(guid)?.Poster;
        if (url is null)
        {
            await next();
            return;
        }

        log.LogDebug("ImageFilter: proxying search result item={ItemId} url={Url}", guid, url);

        try
        {
            var client = http.CreateClient();
            using var res = await client.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                ctx.HttpContext.RequestAborted
            );

            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning(
                    "ImageFilter: upstream returned {Status} for item={ItemId} url={Url}",
                    res.StatusCode,
                    guid,
                    url
                );
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
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "ImageFilter: proxy failed for item={ItemId} url={Url}", guid, url);
            await next();
        }
    }
}
