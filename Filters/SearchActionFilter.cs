using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Gelato.Common;
using Gelato.Configuration;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters
{
    public class SearchActionFilter : IAsyncActionFilter, IOrderedFilter
    {
        private readonly ILibraryManager _library;
        private readonly IItemRepository _repo;
        private readonly IMediaSourceManager _mediaSources;
        private readonly IDtoService _dtoService;
        private readonly ILogger<SearchActionFilter> _log;
        private readonly GelatoManager _manager;

        public SearchActionFilter(
            ILibraryManager library,
            IItemRepository repo,
            IMediaSourceManager mediaSources,
            IDtoService dtoService,
            GelatoManager manager,
            ILogger<SearchActionFilter> log
        )
        {
            _library = library;
            _manager = manager;
            _repo = repo;
            _mediaSources = mediaSources;
            _dtoService = dtoService;
            _log = log;
        }

        public int Order => 1;

        public async Task OnActionExecutionAsync(
            ActionExecutingContext ctx,
            ActionExecutionDelegate next
        )
        {
            ctx.TryGetUserId(out var userId);
            var cfg = GelatoPlugin.Instance!.GetConfig(userId);
            if (
                cfg.DisableSearch
                || !ctx.IsApiSearchAction()
                || !ctx.TryGetActionArgument<string>("searchTerm", out var searchTerm)
                || !await cfg.stremio.IsReady()
            )
            {
                await next();
                return;
            }

            // Strip "local:" prefix if present and pass through to default handler
            if (searchTerm.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
            {
                ctx.ActionArguments["searchTerm"] = searchTerm.Substring(6).Trim();
                await next();
                return;
            }

            // Handle Stremio search
            var requestedTypes = GetRequestedItemTypes(ctx);
            if (requestedTypes.Count == 0)
            {
                //ctx.Result = CreateEmptyResult();
                // let jf handle it
                await next();
                return;
            }

            ctx.TryGetActionArgument("startIndex", out var start, 0);
            ctx.TryGetActionArgument("limit", out var limit, 25);

            var metas = await SearchMetasAsync(searchTerm, requestedTypes, cfg, userId);

            _log.LogInformation(
                "Intercepted /Items search \"{Query}\" types=[{Types}] start={Start} limit={Limit} results={Results}",
                searchTerm,
                string.Join(",", requestedTypes),
                start,
                limit,
                metas.Count
            );

            var dtos = ConvertMetasToDtos(metas);
            var paged = dtos.Skip(start).Take(limit).ToArray();

            ctx.Result = new OkObjectResult(
                new QueryResult<BaseItemDto> { Items = paged, TotalRecordCount = dtos.Count }
            );
        }

        private HashSet<BaseItemKind> GetRequestedItemTypes(ActionExecutingContext ctx)
        {
            var requested = new HashSet<BaseItemKind>(
                new[] { BaseItemKind.Movie, BaseItemKind.Series }
            );

            // Already parsed as BaseItemKind[] by model binder
            if (
                ctx.TryGetActionArgument<BaseItemKind[]>("includeItemTypes", out var includeTypes)
                && includeTypes != null
                && includeTypes.Length > 0
            )
            {
                requested = new HashSet<BaseItemKind>(includeTypes);
                // Only keep Movie and Series
                requested.IntersectWith(new[] { BaseItemKind.Movie, BaseItemKind.Series });
            }

            // Remove excluded types
            if (
                ctx.TryGetActionArgument<BaseItemKind[]>("excludeItemTypes", out var excludeTypes)
                && excludeTypes != null
                && excludeTypes.Length > 0
            )
            {
                requested.ExceptWith(excludeTypes);
            }

            // If mediaTypes=Video, exclude Series
            if (
                ctx.TryGetActionArgument<MediaType[]>("mediaTypes", out var mediaTypes)
                && mediaTypes != null
                && mediaTypes.Contains(MediaType.Video)
            )
            {
                requested.Remove(BaseItemKind.Series);
            }

            return requested;
        }

        private async Task<List<StremioMeta>> SearchMetasAsync(
            string searchTerm,
            HashSet<BaseItemKind> requestedTypes,
            PluginConfiguration cfg,
            Guid userId
        )
        {
            var tasks = new List<Task<IReadOnlyList<StremioMeta>>>();

            if (requestedTypes.Contains(BaseItemKind.Movie) && cfg.MovieFolder is not null)
            {
                tasks.Add(cfg.stremio.SearchAsync(searchTerm, StremioMediaType.Movie));
            }
            else if (requestedTypes.Contains(BaseItemKind.Movie))
            {
                _log.LogWarning("No movie folder found, skipping search");
            }

            if (requestedTypes.Contains(BaseItemKind.Series) && cfg.SeriesFolder is not null)
            {
                tasks.Add(cfg.stremio.SearchAsync(searchTerm, StremioMediaType.Series));
            }
            else if (requestedTypes.Contains(BaseItemKind.Series))
            {
                _log.LogWarning("No series folder found, skipping search");
            }

            var results = (await Task.WhenAll(tasks)).SelectMany(r => r).ToList();

            var filterUnreleased = cfg.FilterUnreleased;
            var bufferDays = cfg.FilterUnreleasedBufferDays;

            if (filterUnreleased)
            {
                results = results
                    .Where(x => x.IsReleased(StremioMediaType.Movie == x.Type ? bufferDays : 0))
                    .ToList();
            }

            return results;
        }

        private List<BaseItemDto> ConvertMetasToDtos(List<StremioMeta> metas)
        {
            // theres a reason i initally disabled all fields but forgot....
            // infuse breaks if we do a small subset. Not sure which field it needs. Prolly mediasources
            var options = new DtoOptions
            {
                //Fields = new[] { ItemFields.ProviderIds, ItemFields.PrimaryImageAspectRatio },
                EnableImages = true,
                EnableUserData = false,
            };

            var dtos = new List<BaseItemDto>(metas.Count);

            foreach (var meta in metas)
            {
                var baseItem = _manager.IntoBaseItem(meta);
                if (baseItem is null)
                    continue;

                var dto = _dtoService.GetBaseItemDto(baseItem, options);
                var stremioUri = StremioUri.FromBaseItem(baseItem);
                dto.Id = stremioUri.ToGuid();
                dtos.Add(dto);

                _manager.SaveStremioMeta(dto.Id, meta);
            }

            return dtos;
        }

        private static OkObjectResult CreateEmptyResult() =>
            new(
                new QueryResult<BaseItemDto>
                {
                    Items = Array.Empty<BaseItemDto>(),
                    TotalRecordCount = 0,
                }
            );
    }
}
