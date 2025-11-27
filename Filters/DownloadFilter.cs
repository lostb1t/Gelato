using System.Net.Http;
using Gelato.Common;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

public sealed class DownloadFilter : IAsyncActionFilter
{
    private readonly ILibraryManager _library;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<DownloadFilter> _log;
    private readonly GelatoManager _manager;
    private readonly IUserManager _userManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMediaSourceManager _mediaSourceManager;

    public DownloadFilter(
        ILibraryManager library,
        IHttpClientFactory http,
        GelatoManager manager,
        IUserManager userManager,
        IMediaSourceManager mediaSourceManager,
        IHttpClientFactory httpClientFactory,
        ILogger<DownloadFilter> log
    )
    {
        _library = library;
        _http = http;
        _log = log;
        _mediaSourceManager = mediaSourceManager;
        _manager = manager;
        _httpClientFactory = httpClientFactory;
        _userManager = userManager;
    }

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        if (ctx.GetActionName() != "GetDownload")
        {
            await next();
            return;
        }

        if (!ctx.TryGetRouteGuid(out var guid))
        {
            await next();
            return;
        }

        var principal = ctx.HttpContext.User;
        var userIdStr = ctx
            .HttpContext.User.Claims.FirstOrDefault(c =>
                c.Type == "UserId" || c.Type == "Jellyfin-UserId"
            )
            ?.Value;

        User? user = null;
        if (Guid.TryParse(userIdStr, out var userId))
        {
            user = _userManager.GetUserById(userId);
        }

        if (user is not null)
        {
            var mediaSourceIdStr = ctx.HttpContext.Items["MediaSourceId"] as string;
            var hasMediaSourceId = Guid.TryParse(mediaSourceIdStr, out var mediaSourceId);

            var item = _library.GetItemById<Video>(hasMediaSourceId ? mediaSourceId : guid, user);

            if (_manager.IsStremio(item))
            {
                var path = item.Path;

                // some clients do not send mediasource id. the use the itemid in the query
                if (!hasMediaSourceId || !_manager.IsStream(item))
                {
                    path = _mediaSourceManager.GetStaticMediaSources(item, true, user)[0].Path;
                }

                var client = _httpClientFactory.CreateClient();

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
                    var uri = new Uri(path);
                    fileName = Path.GetFileName(uri.AbsolutePath);
                    if (string.IsNullOrWhiteSpace(fileName))
                        fileName = "download";
                }

                if (resp.Content.Headers.ContentLength is long len)
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
            // }
        }

        await next();
        return;
    }
}
