using System.Runtime.ExceptionServices;
using Gelato.Config;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

public class SearchActionFilter(
    IDtoService dtoService,
    GelatoManager manager,
    ILogger<SearchActionFilter> log
) : IAsyncActionFilter, IOrderedFilter
{
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
            // GetConfig falls back to a bare configuration when the Gelato url is unset,
            // which leaves Stremio null — let the request through untouched.
            || cfg.Stremio is not { } stremio
            || !await stremio.IsReady()
        )
        {
            await next();
            return;
        }

        // Strip "local:" prefix if present and pass through to default handler
        if (searchTerm.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
        {
            ctx.ActionArguments["searchTerm"] = searchTerm[6..].Trim();
            await next();
            return;
        }

        // Handle Stremio search
        var requestedTypes = GetRequestedItemTypes(ctx);
        if (requestedTypes.Count == 0)
        {
            await next();
            return;
        }

        ctx.TryGetActionArgument("startIndex", out var start, 0);
        ctx.TryGetActionArgument("limit", out var limit, 25);

        var metas = await SearchMetasAsync(searchTerm, requestedTypes, cfg, stremio, userId);

        log.LogInformation(
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
        var requested = new HashSet<BaseItemKind>([BaseItemKind.Movie, BaseItemKind.Series]);

        // Already parsed as BaseItemKind[] by model binder
        if (
            ctx.TryGetActionArgument<BaseItemKind[]>("includeItemTypes", out var includeTypes)
            && includeTypes is { Length: > 0 }
        )
        {
            requested = new HashSet<BaseItemKind>(includeTypes);
            // Only keep Movie and Series
            requested.IntersectWith([BaseItemKind.Movie, BaseItemKind.Series]);
        }

        // Remove excluded types
        if (
            ctx.TryGetActionArgument<BaseItemKind[]>("excludeItemTypes", out var excludeTypes)
            && excludeTypes is { Length: > 0 }
        )
        {
            requested.ExceptWith(excludeTypes);
        }

        // If mediaTypes=Video, exclude Series
        if (
            ctx.TryGetActionArgument<MediaType[]>("mediaTypes", out var mediaTypes)
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
        GelatoStremioProvider stremio,
        Guid userId
    )
    {
        var tasks = new List<(StremioMediaType Type, Task<IReadOnlyList<StremioMeta>> Task)>();
        var movieFolder = cfg.MovieFolder ?? manager.TryGetMovieFolder(userId);
        var seriesFolder = cfg.SeriesFolder ?? manager.TryGetSeriesFolder(userId);

        // Keep hot config in sync for subsequent searches in this request window.
        cfg.MovieFolder = movieFolder;
        cfg.SeriesFolder = seriesFolder;

        if (requestedTypes.Contains(BaseItemKind.Movie) && movieFolder is not null)
        {
            tasks.Add(
                (StremioMediaType.Movie, stremio.SearchAsync(searchTerm, StremioMediaType.Movie))
            );
        }
        else if (requestedTypes.Contains(BaseItemKind.Movie))
        {
            log.LogWarning(
                "No movie folder found, please add your gelato path to a library and rescan. skipping search"
            );
        }

        if (requestedTypes.Contains(BaseItemKind.Series) && seriesFolder is not null)
        {
            tasks.Add(
                (StremioMediaType.Series, stremio.SearchAsync(searchTerm, StremioMediaType.Series))
            );
        }
        else if (requestedTypes.Contains(BaseItemKind.Series))
        {
            log.LogWarning(
                "No series folder found, please add your gelato path to a library and rescan. skipping search"
            );
        }

        // Task.WhenAll used to throw for the first catalog that failed, which threw out of the filter and made
        // Jellyfin answer the whole request with HTTP 500 — the results of a catalog that did answer included.
        // Awaited one by one now (they all run, the tasks are started above), so a failure only costs its own
        // catalog. A search where no catalog answered still fails the request: an empty or library-only list
        // would look like a successful search to the client, and clients cache it.
        var results = new List<StremioMeta>();
        var failures = new List<Exception>();
        foreach (var (type, task) in tasks)
        {
            try
            {
                results.AddRange(await task);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
                log.LogWarning(
                    ex,
                    "Search \"{Query}\" failed for the {MediaType} catalog",
                    searchTerm,
                    type
                );
            }
        }

        if (failures.Count > 0 && failures.Count == tasks.Count)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();

        var filterUnreleased = cfg.FilterUnreleased;
        var bufferDays = cfg.FilterUnreleasedBufferDays;

        if (filterUnreleased)
        {
            results = results.Where(x => x.IsReleased(bufferDays)).ToList();
        }

        return results;
    }

    private List<BaseItemDto> ConvertMetasToDtos(List<StremioMeta> metas)
    {
        // theres a reason i initally disabled all fields but forgot....
        // infuse breaks if we do a small subset. Not sure which field it needs. Prolly mediasources
        var options = new DtoOptions { EnableImages = true, EnableUserData = false };

        var dtos = new List<BaseItemDto>(metas.Count);

        // The movie and series catalogs are searched separately and their results concatenated,
        // but an addon may return the same title under both — a series showing up in the movie
        // results, say. The ids are deterministic, so the same title yields the same id twice
        // and the client renders it twice. Keep the first occurrence and drop later repeats.
        var seen = new HashSet<Guid>();

        foreach (var meta in metas)
        {
            var baseItem = manager.IntoBaseItem(meta);
            if (baseItem is null)
                continue;

            var dto = dtoService.GetBaseItemDto(baseItem, options);
            var stremioUri = StremioUri.FromBaseItem(baseItem);
            dto.Id = stremioUri.ToGuid();

            if (!seen.Add(dto.Id))
                continue;

            dtos.Add(dto);

            manager.SaveStremioMeta(dto.Id, meta);
        }

        return dtos;
    }
}
