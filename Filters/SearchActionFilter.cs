using System.Runtime.ExceptionServices;
using Gelato.Config;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

public class SearchActionFilter(
    IDtoService dtoService,
    GelatoManager manager,
    IUserManager userManager,
    ILibraryManager libraryManager,
    IDbContextFactory<JellyfinDbContext> dbFactory,
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

        var results = ConvertMetasToDtos(metas);
        var page = results.Skip(start).Take(limit).ToList();
        var paged = await UseLibraryItemsAsync(page, userId, ctx).ConfigureAwait(false);

        ctx.Result = new OkObjectResult(
            new QueryResult<BaseItemDto> { Items = paged, TotalRecordCount = results.Count }
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

    /// <summary>
    /// A title that is already in the library is returned as the library item, so the client
    /// gets its real id and the user's played, favorite and resume state. A synthetic result
    /// carries no user data, so a watched title shows as unwatched in search.
    /// </summary>
    /// <remarks>
    /// Most results are not in the library, so the lookup has to be cheap for a miss: one query
    /// for the provider ids of the whole page and one for the items behind the hits, however
    /// many results the page has.
    /// </remarks>
    private async Task<BaseItemDto[]> UseLibraryItemsAsync(
        List<(BaseItemDto Dto, BaseItem Item)> page,
        Guid userId,
        ActionExecutingContext ctx
    )
    {
        if (
            page.Count == 0
            || userId == Guid.Empty
            || userManager.GetUserById(userId) is not { } user
        )
        {
            return page.Select(r => r.Dto).ToArray();
        }

        var libraryItems = await FindLibraryItemsAsync(
                page.Select(r => r.Item),
                user,
                ctx.HttpContext.RequestAborted
            )
            .ConfigureAwait(false);
        if (libraryItems.Count == 0)
            return page.Select(r => r.Dto).ToArray();

        // A real item gets what Jellyfin's own search would return: the fields the client asked
        // for. All fields, as the synthetic results use, makes a series count its episodes.
        ctx.TryGetActionArgument<ItemFields[]>("fields", out var fields, []);
        ctx.TryGetActionArgument<bool?>("enableUserData", out var enableUserData);
        var options = new DtoOptions
        {
            Fields = fields,
            EnableImages = true,
            EnableUserData = enableUserData ?? true,
        };

        return page.Select(r =>
                FindMatch(libraryItems, r.Item) is { } existing
                    ? dtoService.GetBaseItemDto(existing, options, user)
                    : r.Dto
            )
            // Two metas with different ids can be the same library item.
            .DistinctBy(dto => dto.Id)
            .ToArray();
    }

    private async Task<IReadOnlyList<BaseItem>> FindLibraryItemsAsync(
        IEnumerable<BaseItem> candidates,
        User user,
        CancellationToken ct
    )
    {
        var providerIds = candidates.SelectMany(c => c.ProviderIds).ToList();
        var names = providerIds.Select(p => p.Key).Distinct().ToArray();
        var values = providerIds.Select(p => p.Value).Distinct().ToArray();
        if (values.Length == 0)
            return [];

        Guid[] itemIds;
        var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            itemIds = await db
                .BaseItemProviders.AsNoTracking()
                .Where(p => names.Contains(p.ProviderId) && values.Contains(p.ProviderValue))
                .Select(p => p.ItemId)
                .Distinct()
                .ToArrayAsync(ct)
                .ConfigureAwait(false);
        }

        if (itemIds.Length == 0)
            return [];

        return libraryManager.GetItemList(
            new InternalItemsQuery(user)
            {
                ItemIds = itemIds,
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
                ExcludeTags = [GelatoManager.StreamTag],
                IsDeadPerson = true, // skip filter marker
            }
        );
    }

    /// <summary>Same rule as <see cref="GelatoManager.FindExistingItem"/>, on loaded items.</summary>
    private static BaseItem? FindMatch(IReadOnlyList<BaseItem> libraryItems, BaseItem candidate)
    {
        var kind = candidate.GetBaseItemKind();
        return libraryItems.FirstOrDefault(item =>
            item.GetBaseItemKind() == kind
            && !(item is Video video && video.IsStream())
            && candidate.ProviderIds.Any(id =>
                string.Equals(
                    item.GetProviderId(id.Key),
                    id.Value,
                    StringComparison.OrdinalIgnoreCase
                )
            )
        );
    }

    private List<(BaseItemDto Dto, BaseItem Item)> ConvertMetasToDtos(List<StremioMeta> metas)
    {
        // theres a reason i initally disabled all fields but forgot....
        // infuse breaks if we do a small subset. Not sure which field it needs. Prolly mediasources
        var options = new DtoOptions { EnableImages = true, EnableUserData = false };

        var dtos = new List<(BaseItemDto Dto, BaseItem Item)>(metas.Count);

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

            dtos.Add((dto, baseItem));

            manager.SaveStremioMeta(dto.Id, meta);
        }

        return dtos;
    }
}
