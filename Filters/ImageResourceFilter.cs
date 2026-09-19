using Gelato.Decorators;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

/// <summary>
/// Proxies image requests for search results (non-library gelato items), and serves a stream
/// row's images from its movie/episode. Library item images are handled by ImageProcessorDecorator.
/// </summary>
public sealed class ImageResourceFilter(
    IHttpClientFactory http,
    GelatoManager manager,
    ILibraryManager libraryManager,
    IApplicationPaths appPaths,
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
                // HEAD requests share these action names; the Head* names in ImageController are
                // route names.
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

        // The search result was opened and inserted: its images are the item's now.
        if (manager.GetInsertedId(guid) is { } insertedId)
        {
            routeValues["itemId"] = insertedId.ToString("N");
            await NotFoundOrNext(ctx, next, insertedId);
            return;
        }

        // A stream row has no images of its own; the DTO hands out its movie's image tags.
        if (
            libraryManager.GetItemById(guid) is Video { PrimaryVersionId: { } primaryId } row
            && row.HasStreamTag()
        )
        {
            routeValues["itemId"] = primaryId.ToString("N");
            await NotFoundOrNext(ctx, next, primaryId);
            return;
        }

        // Only handle cached search results — library items go through ProcessImage
        var url = manager.GetStremioMeta(guid)?.Poster;
        if (url is null)
        {
            await NotFoundOrNext(ctx, next, guid);
            return;
        }

        log.LogDebug(
            "ImageFilter: proxying search result item={ItemId} url={Url}",
            guid,
            Redact.Url(url)
        );

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
                    Redact.Url(url)
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
            log.LogWarning(
                ex,
                "ImageFilter: proxy failed for item={ItemId} url={Url}",
                guid,
                Redact.Url(url)
            );
            await next();
        }
    }

    /// <summary>
    /// Answers not found for an image whose remote failed recently, and hands the request on for
    /// everything else. ProcessImage ends in the same not found once it has looked at the empty
    /// placeholder; stopping here keeps the repeat out of the controller and out of the log.
    /// </summary>
    private async Task NotFoundOrNext(
        ResourceExecutingContext ctx,
        ResourceExecutionDelegate next,
        Guid itemId
    )
    {
        if (IsKnownDeadImage(ctx, itemId))
        {
            ctx.Result = new NotFoundResult();
            return;
        }

        await next();
    }

    private bool IsKnownDeadImage(ResourceExecutingContext ctx, Guid itemId)
    {
        var values = ctx.RouteData.Values;
        if (
            !Enum.TryParse<ImageType>(values["imageType"]?.ToString(), true, out var type)
            || libraryManager.GetItemById(itemId) is not { } item
        )
            return false;

        _ = int.TryParse(values["imageIndex"]?.ToString(), out var index);
        if (item.GetImageInfo(type, index)?.Path is not { } path)
            return false;

        var file = new FileInfo(path);
        if (file.Exists && file.Length > 0)
            return false;

        var urlFile = ImageProcessorDecorator.ResolveUrlFile(appPaths, itemId, path, type, index);
        if (urlFile is null)
            return false;

        try
        {
            return ImageProcessorDecorator.IsKnownDead(File.ReadAllText(urlFile).Trim());
        }
        catch (IOException)
        {
            return false;
        }
    }
}
