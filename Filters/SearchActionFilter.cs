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
// using Gelato.Common;
using Jellyfin.Data.Enums;
using Gelato.Common;

namespace Gelato.Filters
{
    public class SearchActionFilter : IAsyncActionFilter, IOrderedFilter
    {
        private readonly ILibraryManager _library;
        private readonly IItemRepository _repo;
        private readonly IMediaSourceManager _mediaSources;
        private readonly IDtoService _dtoService;
        private readonly GelatoStremioProvider _provider;
        private readonly ILogger<SearchActionFilter> _log;
        private readonly GelatoManager _manager;

        public SearchActionFilter(
            ILibraryManager library,
            IItemRepository repo,
            IMediaSourceManager mediaSources,
            IDtoService dtoService,
            GelatoManager manager,
            GelatoStremioProvider provider,
            ILogger<SearchActionFilter> log)
        {
            _library = library;
            _manager = manager;
            _repo = repo;
            _mediaSources = mediaSources;
            _dtoService = dtoService;
            _provider = provider;
            _log = log;
        }

        public int Order => throw new NotImplementedException();

        public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
        {
            if (!await _provider.IsReady())
            {
                await next();
                return;
            }

            var http = ctx.HttpContext;

            var hasSearch = http.Request.Query.Keys
                .Any(k => string.Equals(k, "SearchTerm", StringComparison.OrdinalIgnoreCase) &&
                          !string.IsNullOrWhiteSpace(http.Request.Query[k]));
            if (!hasSearch)
            {
                await next();
                return;
            }

            var requested = new HashSet<BaseItemKind>();
            if (http.Request.Query.TryGetValue("IncludeItemTypes", out var includeVal) && !string.IsNullOrWhiteSpace(includeVal))
            {
                foreach (var raw in includeVal.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (Enum.TryParse<BaseItemKind>(raw, true, out var mt))
                    {
                        if (mt == BaseItemKind.Movie || mt == BaseItemKind.Series)
                            requested.Add(mt);
                    }
                }
            }
            else
            {
                await next();
                return;
            }

            if (requested.Count == 0)
            {
                ctx.Result = new OkObjectResult(new QueryResult<BaseItemDto>
                {
                    Items = Array.Empty<BaseItemDto>(),
                    TotalRecordCount = 0
                });
                return;
            }

            int start = 0;
            int limit = int.MaxValue;
            if (http.Request.Query.TryGetValue("StartIndex", out var startVal) &&
                int.TryParse(startVal, out var si) && si >= 0) start = si;
            if (http.Request.Query.TryGetValue("Limit", out var limitVal) &&
                int.TryParse(limitVal, out var lim) && lim > 0) limit = lim;

            var q = http.Request.Query.First(kv => string.Equals(kv.Key, "SearchTerm", StringComparison.OrdinalIgnoreCase)).Value.ToString();
            _log.LogDebug("Gelato: intercepted /Items search \"{Query}\" types=[{Types}] start={Start} limit={Limit}",
                          q, string.Join(",", requested.Select(r => r.ToString())), start, limit);

            var metas = new List<StremioMeta>(256);

            if (requested.Contains(BaseItemKind.Movie))
            {
                var movies = await _provider.SearchAsync(q, StremioMediaType.Movie);
                metas.AddRange(movies);
            }

            if (requested.Contains(BaseItemKind.Series))
            {
                var series = await _provider.SearchAsync(q, StremioMediaType.Series);
                metas.AddRange(series);
            }

            var options = new DtoOptions();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dtos = new List<BaseItemDto>(metas.Count);

            foreach (var s in metas)
            {
                var baseItem = _provider.IntoBaseItem(s);
                if (baseItem is null) continue;

                var stremioKey = baseItem.GetProviderId("stremio");
                if (string.IsNullOrWhiteSpace(stremioKey) || !seen.Add(stremioKey))
                    continue;

                var dto = _dtoService.GetBaseItemDto(baseItem, options);
                var stremioUri = StremioUri.LoadFromString(stremioKey);
                dto.Id = stremioUri.ToGuid();
                _manager.SaveStremioMeta(dto.Id, s);
                // _log.LogInformation($"Gelato: Search found {stremioUri.ToString()}, {stremioUri.ToCompactString()}");

                // dto.Id = GuidCodec.EncodeString(StremioId.ToCompactId(stremioKey));
                dtos.Add(dto);
            }

            var paged = dtos.Skip(start).Take(limit).ToArray();

            ctx.Result = new OkObjectResult(new QueryResult<BaseItemDto>
            {
                Items = paged,
                TotalRecordCount = dtos.Count
            });
            return;
        }
    }
}