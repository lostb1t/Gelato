using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Gelato.Filters;

public sealed class DownloadFilter(
    ILibraryManager library,
    GelatoManager manager,
    IUserManager userManager,
    IMediaSourceManager mediaSourceManager,
    IHttpClientFactory httpClientFactory
) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        if (ctx.GetActionName() != "GetDownload" || !ctx.TryGetRouteGuid(out var guid))
        {
            await next();
            return;
        }

        // An API key authenticates without a user, and Jellyfin still puts a UserId claim on the
        // request: the empty guid. GetUserById throws on that, which turned every download made
        // with an API key into a 400 (lostb1t/Gelato#187). TryGetUserId leaves the user null
        // instead, and Jellyfin's own GetDownload carries a null user through, so this does too:
        // without a user the first of all the item's streams is served, not one user's.
        User? user = ctx.TryGetUserId(out var userId) ? userManager.GetUserById(userId) : null;

        var mediaSourceIdStr = ctx.HttpContext.Items["MediaSourceId"] as string;
        var hasMediaSourceId = Guid.TryParse(mediaSourceIdStr, out var mediaSourceId);

        var item = library.GetItemById<Video>(hasMediaSourceId ? mediaSourceId : guid, user);

        if (item != null && manager.IsStremio(item))
        {
            var path = item.Path;

            // some clients do not send mediasource id. the use the itemid in the query
            if (!hasMediaSourceId || !item.IsStream())
            {
                path = mediaSourceManager.GetStaticMediaSources(item, true, user)[0].Path;
            }

            // Streams are only synced for a user. A title nobody has opened yet has none without
            // one, and its own gelato:// path is nothing to fetch: Jellyfin answers that.
            if (Uri.TryCreate(path, UriKind.Absolute, out var streamUri) && IsHttp(streamUri))
            {
                var client = httpClientFactory.CreateClient();

                var resp = await client.GetAsync(
                    path,
                    HttpCompletionOption.ResponseHeadersRead,
                    CancellationToken.None
                );

                resp.EnsureSuccessStatusCode();

                ctx.HttpContext.Response.RegisterForDispose(resp);

                var stream = await resp.Content.ReadAsStreamAsync(CancellationToken.None);

                var contentType =
                    resp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";

                var fileName = resp.Content.Headers.ContentDisposition?.FileName?.Trim('"');
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    fileName = Path.GetFileName(streamUri.AbsolutePath);
                    if (string.IsNullOrWhiteSpace(fileName))
                        fileName = "download";
                }

                if (resp.Content.Headers.ContentLength is { } len)
                {
                    ctx.HttpContext.Response.ContentLength = len;
                }

                ctx.Result = new FileStreamResult(stream, contentType)
                {
                    FileDownloadName = fileName,
                    EnableRangeProcessing = true,
                };
                return;
            }
        }

        await next();
    }

    private static bool IsHttp(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
}
