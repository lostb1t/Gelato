using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

/// <summary>
///  Replaces image requests from stremio sources
/// </summary>
public sealed class ImageResourceFilter(
    IHttpClientFactory http,
    GelatoManager manager,
    ILibraryManager libraryManager,
    ILogger<ImageResourceFilter> log
) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(
        ResourceExecutingContext ctx,
        ResourceExecutionDelegate next
    )
    {
        if (ctx.ActionDescriptor is not ControllerActionDescriptor { ActionName: "GetItemImage" })
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

        // Try cached meta first (search results)
        var url = manager.GetStremioMeta(guid)?.Poster;

        // Fall back to persisted provider IDs for library items
        if (url is null)
        {
            var item = libraryManager.GetItemById(guid);
            if (item is not null && item.IsGelato())
            {
                // Try type-specific key first (from ProviderManagerDecorator),
                // then the primary poster key (from IntoBaseItem)
                var imageType = routeValues.TryGetValue("imageType", out var imgType)
                    ? imgType?.ToString()
                    : null;

                var imageIndex =
                    routeValues.TryGetValue("imageIndex", out var idxVal)
                    && int.TryParse(idxVal?.ToString(), out var idx)
                    && idx > 0
                        ? idx
                        : (int?)null;

                if (imageType is not null)
                {
                    var key = $"GelatoImage:{imageType}";
                    if (imageIndex is not null)
                    {
                        key += $":{imageIndex}";
                    }

                    url = item.GetProviderId(key);
                }

                url ??= item.GetProviderId("GelatoPoster");
            }
        }

        if (url is null)
        {
            await next();
            return;
        }

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
            log.LogWarning(ex, "Image proxy failed for item {ItemId}", guid);
            await next();
        }
    }
}
