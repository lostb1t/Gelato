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

        // A client asks for every type it wants in one request: the web client's global search
        // sends Movie, Series, Episode, BoxSet, TvChannel and more together. Answering all of it
        // with the addon's movies and series dropped the rest, so a channel was only ever found
        // inside Live TV, where the client asks for TvChannel alone and the search never gets
        // this far (lostb1t/Gelato#162). Let Jellyfin answer for the types the addon has nothing
        // to say about and put its results after the addon's.
        var (executed, localItems, localTotal) = await SearchOtherTypesAsync(
            ctx,
            next,
            start + limit
        );

        log.LogInformation(
            "Intercepted /Items search \"{Query}\" types=[{Types}] start={Start} limit={Limit} results={Results} library={Library}",
            searchTerm,
            string.Join(",", requestedTypes),
            start,
            limit,
            metas.Count,
            localTotal
        );

        var dtos = ConvertMetasToDtos(metas);
        var paged = dtos.Concat(localItems).Skip(start).Take(limit).ToArray();

        var result = new OkObjectResult(
            new QueryResult<BaseItemDto>
            {
                Items = paged,
                TotalRecordCount = dtos.Count + localTotal,
            }
        );

        // Setting ctx.Result only short-circuits while the action has not run; once next() has
        // been awaited the answer is the executed context's.
        if (executed is null)
            ctx.Result = result;
        else
            executed.Result = result;
    }

    /// <summary>
    /// Runs the untouched Jellyfin search for the item types the request asked for that the addon
    /// does not answer for, and returns its items and total. Nothing runs, and the result is empty,
    /// when the request asked for movies and series only.
    /// </summary>
    private async Task<(
        ActionExecutedContext? Executed,
        IReadOnlyList<BaseItemDto> Items,
        int Total
    )> SearchOtherTypesAsync(ActionExecutingContext ctx, ActionExecutionDelegate next, int upTo)
    {
        if (GetPassThroughExcludes(ctx) is not { } excludeTypes)
            return (null, [], 0);

        // Jellyfin pages its own answer, so ask it for everything up to the end of the page that
        // is being served and page the concatenation here.
        ctx.ActionArguments["excludeItemTypes"] = excludeTypes;
        ctx.ActionArguments["startIndex"] = 0;
        ctx.ActionArguments["limit"] = upTo;

        var executed = await next();

        // The library half failing is no reason to lose the addon's results: the search answers
        // with those alone, the way it did before it asked for the other types at all.
        if (executed.Exception is { } ex)
        {
            log.LogWarning(ex, "The library search for the other item types failed");
            executed.ExceptionHandled = true;
            return (executed, [], 0);
        }

        if (
            executed.Result is ObjectResult { Value: QueryResult<BaseItemDto> local }
            && local.Items is { } items
        )
        {
            return (executed, items, local.TotalRecordCount);
        }

        return (executed, [], 0);
    }

    /// <summary>
    /// The excludeItemTypes the pass-through search runs with: the request's own excludes plus the
    /// types the addon answers for. Null when the request named movies and series only, so there is
    /// nothing left for Jellyfin to look for.
    /// </summary>
    private static BaseItemKind[]? GetPassThroughExcludes(ActionExecutingContext ctx)
    {
        ctx.TryGetActionArgument<BaseItemKind[]>("includeItemTypes", out var includeTypes);
        if (
            includeTypes is { Length: > 0 }
            && includeTypes.All(t => t is BaseItemKind.Movie or BaseItemKind.Series)
        )
        {
            return null;
        }

        ctx.TryGetActionArgument<BaseItemKind[]>("excludeItemTypes", out var excludeTypes);
        return (excludeTypes ?? [])
            .Concat([BaseItemKind.Movie, BaseItemKind.Series])
            .Distinct()
            .ToArray();
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
