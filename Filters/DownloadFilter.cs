using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

public sealed class DownloadFilter(
    ILibraryManager library,
    GelatoManager manager,
    IUserManager userManager,
    IMediaSourceManager mediaSourceManager,
    IHttpClientFactory httpClientFactory,
    ILogger<DownloadFilter> log
) : IAsyncActionFilter
{
    // How long one stream gets to answer with its headers, and how many of the item's streams are
    // tried. A dead debrid link used to hold the download for HttpClient's 100 s and end in a 500;
    // the body itself streams without a limit.
    private static readonly TimeSpan HeadersTimeout = TimeSpan.FromSeconds(10);
    private const int MaxStreams = 3;

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
            // A client that named a stream row, as the item or as its media source, gets that
            // stream or nothing. For the movie or episode its streams are tried in Gelato's order
            // (a row named as the item used to get the first stream of its title).
            var paths =
                item.IsStream()
                    ? [item.Path]
                    : mediaSourceManager
                        .GetStaticMediaSources(item, true, user)
                        .Select(s => s.Path)
                        .ToList();

            // Streams are only synced for a user. A title nobody has opened yet has none without
            // one, and its own gelato:// path is nothing to fetch: Jellyfin answers that.
            var streamUris = paths
                .Select(p => Uri.TryCreate(p, UriKind.Absolute, out var u) ? u : null)
                .Where(u => u != null && IsHttp(u!))
                .Take(MaxStreams)
                .ToList();

            if (streamUris.Count > 0)
            {
                var aborted = ctx.HttpContext.RequestAborted;
                var client = httpClientFactory.CreateClient();

                for (var i = 0; i < streamUris.Count; i++)
                {
                    var streamUri = streamUris[i]!;
                    var resp = await TryGetAsync(client, streamUri, aborted);
                    if (resp is null || !resp.IsSuccessStatusCode)
                    {
                        // No URL in the log: stream links carry the debrid key.
                        log.LogWarning(
                            "Download of {Item}: stream {Index} of {Count} did not answer ({Reason})",
                            item.Name,
                            i + 1,
                            streamUris.Count,
                            resp is null ? "no response" : $"status {(int)resp.StatusCode}"
                        );
                        resp?.Dispose();
                        continue;
                    }

                    ctx.HttpContext.Response.RegisterForDispose(resp);

                    var stream = await resp.Content.ReadAsStreamAsync(aborted);

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

                aborted.ThrowIfCancellationRequested();
                ctx.Result = new ContentResult
                {
                    StatusCode = StatusCodes.Status502BadGateway,
                    Content = "None of the title's streams answered. Try again later.",
                    ContentType = "text/plain",
                };
                return;
            }
        }

        await next();
    }

    // Null when the stream gives no headers in time or cannot be reached; the request's own
    // abort still throws.
    private static async Task<HttpResponseMessage?> TryGetAsync(
        HttpClient client,
        Uri uri,
        CancellationToken aborted
    )
    {
        // Disposing the source when the headers are in stops its timer, so the body is not cut.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        cts.CancelAfter(HeadersTimeout);
        try
        {
            return await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        }
        catch (Exception ex)
            when (!aborted.IsCancellationRequested
                && ex is HttpRequestException or OperationCanceledException
            )
        {
            return null;
        }
    }

    private static bool IsHttp(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
}
