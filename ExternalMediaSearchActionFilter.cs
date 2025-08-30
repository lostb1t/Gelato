using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;

using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Jellyfin.Plugin.ExternalMedia.Common;

namespace Jellyfin.Plugin.ExternalMedia
{
    /// Intercepts item searches so the controller doesn't touch the DB.
    public class ExternalMediaSearchActionFilter : IAsyncActionFilter
    {
        private readonly ILibraryManager _library;
        private readonly IItemRepository _repo;
        private readonly IMediaSourceManager _mediaSources;
        private readonly IDtoService _dtoService;
        private readonly ExternalMediaStremioProvider _provider;
        private readonly ILogger<ExternalMediaSearchActionFilter> _log;

        public ExternalMediaSearchActionFilter(
            ILibraryManager library,
            IItemRepository repo,
            IMediaSourceManager mediaSources,
            IDtoService dtoService,
            ExternalMediaStremioProvider provider,
            ILogger<ExternalMediaSearchActionFilter> log)
        {
            _library = library;
            _repo = repo;
            _mediaSources = mediaSources;
            _dtoService = dtoService;
            _provider = provider;
            _log = log;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            try
            {
                if (!await _provider.IsReady())
                {
                    await next();
                    return;
                }
            }
            catch (Exception ex)
            {

            }

            var http = context.HttpContext;
            var path = http.Request.Path.Value?.ToLowerInvariant() ?? "";
            //_log.LogInformation("ExternalMedia: hook");
            // Only care about GET /Items style endpoints
            if (!http.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase) ||
                !path.StartsWith("/items", StringComparison.Ordinal))
            {
                await next();
                return;
            }

            // Jellyfin search param is "SearchTerm" (case-insensitive)
            var hasSearch = http.Request.Query.Keys
                .Any(k => string.Equals(k, "SearchTerm", StringComparison.OrdinalIgnoreCase) &&
                          !string.IsNullOrWhiteSpace(http.Request.Query[k]));

            if (!hasSearch)
            {
                // Not a search → let it through
                await next();
                return;
            }

            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Movie", "Series" };
            http.Request.Query.TryGetValue("IncludeItemTypes", out var includeVal);
            var includeRaw = includeVal.ToString();

            var requested = includeRaw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => allowed.Contains(t))
                .ToArray();

            if (requested.Length == 0)
            {
                context.Result = new OkObjectResult(new QueryResult<BaseItemDto>
                {
                    Items = Array.Empty<BaseItemDto>(),
                    TotalRecordCount = 0
                });
                return;
            }

            // At this point: it's a search. Do NOT call next(); short-circuit.
            var q = http.Request.Query.First(kv => string.Equals(kv.Key, "SearchTerm", StringComparison.OrdinalIgnoreCase)).Value.ToString();
            _log.LogInformation("ExternalMedia: intercepted /Items search \"{Query}\" → short-circuiting to client/Stremio.", q);

            var items = await _provider.SearchAsync(q);


            var options = new DtoOptions();
            var dtos = new List<BaseItemDto>();
            foreach (var s in items)
            {

                //   var imdbId = s.Imdb_Id;


                var b = _provider.IntoBaseItem(s);

                if (b is not null)
                {
                    var id = b.GetProviderId("stremio");
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }
                    var dto = _dtoService.GetBaseItemDto(b, options);
                    // _log.LogInformation("ExternalMedia: ID {Guid}", id);
                    _log.LogInformation("ExternalMedia: ID {Guid}", id);
                    dto.Id = GuidCodec.EncodeString(StremioId.Encode(id));
                    dtos.Add(dto);

                    //_log.LogInformation("ExternalMedia: {Query}", s.Name);
                }
            }

            // Option A: return an empty Jellyfin-shaped page so the client won’t crash
            var empty = new QueryResult<BaseItemDto>
            {
                Items = dtos,
                TotalRecordCount = dtos.Count
            };
            _log.LogInformation("ExternalMedia: done");

            // Optional: hint to client that this came from external filter
            //  http.Response.Headers["X-External-Search"] = "stremio";
            // http.Response.Headers["Cache-Control"] = "no-store";

            context.Result = new OkObjectResult(empty);
            return;

            // Option B (alternative): 307 redirect to your own /external/search endpoint
            // context.Result = new RedirectResult($"/Plugins/ExternalMedia/Search?query={Uri.EscapeDataString(q)}", false);
            // return;
        }
    }
}