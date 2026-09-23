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
    IUserManager userManager,
    ILibraryManager libraryManager,
    ISearchManager searchManager,
    IDbContextFactory<JellyfinDbContext> dbFactory,
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
        LimitToScope(ctx, cfg, userId, requestedTypes);
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
        // this far (lostb1t/Gelato#162). Let Jellyfin answer for everything it holds and put its
        // results after the addon's.
        var (executed, localItems, localTotal) = await SearchLibraryAsync(ctx, next, start + limit);

        // The addon's result for a title the library already has and the library's own item are
        // the same title twice. The addon's half answers with the library's item where there is
        // one, in the result's own place — the addon's order is the search's relevance, and an
        // owned title belongs where its result was — so the library half only has to leave those
        // items out. What stays in it is what the addon did not answer for: a file the library
        // holds that no catalog carries.
        ctx.TryGetActionArgument<ItemFields[]>("fields", out var fields, []);
        var (dtos, covered) = await ConvertMetasToDtos(
            metas,
            userId,
            fields,
            ctx.HttpContext.RequestAborted
        );
        var libraryItems = localItems.Where(i => !covered.Contains(i.Id)).ToArray();
        var paged = dtos.Concat(libraryItems).Skip(start).Take(limit).ToArray();

        // An estimate, the way it was before: the library's total counts the items the addon's
        // half already answers with, and only the page that was fetched shows which those are.
        // Counting them all out keeps the number the same from page to page, which is what a
        // client pages by.
        var total = Math.Max(
            dtos.Count + localTotal - covered.Count,
            dtos.Count + libraryItems.Length
        );

        // addon: what the addon's half answers with, after invalid and duplicate results are
        // dropped; owned: of those, titles the library already has.
        log.LogInformation(
            "Intercepted /Items search \"{Query}\" types=[{Types}] start={Start} limit={Limit} addon={Addon} owned={Owned} library={Library} returned={Returned} total={Total}",
            searchTerm,
            string.Join(",", requestedTypes),
            start,
            limit,
            dtos.Count,
            covered.Count,
            localTotal,
            paged.Length,
            total
        );

        var result = new OkObjectResult(
            new QueryResult<BaseItemDto> { Items = paged, TotalRecordCount = total }
        );

        // Setting ctx.Result only short-circuits while the action has not run; once next() has
        // been awaited the answer is the executed context's.
        if (executed is null)
            ctx.Result = result;
        else
            executed.Result = result;
    }

    /// <summary>
    /// Runs the untouched Jellyfin search for the item types the request asked for and returns its
    /// items and total. It answers for every type, movies and series included: the library's own
    /// copy of a title the addon answered for is taken out of the concatenation afterwards, which
    /// leaves the files the library holds that no catalog carries findable.
    /// </summary>
    private async Task<(
        ActionExecutedContext? Executed,
        IReadOnlyList<BaseItemDto> Items,
        int Total
    )> SearchLibraryAsync(ActionExecutingContext ctx, ActionExecutionDelegate next, int upTo)
    {
        // Jellyfin pages its own answer, so ask it for everything up to the end of the page that
        // is being served and page the concatenation here.
        ctx.ActionArguments["startIndex"] = 0;
        ctx.ActionArguments["limit"] = upTo;

        var executed = await next();

        // The library half failing is no reason to lose the addon's results: the search answers
        // with those alone, the way it did before it asked the library at all.
        if (executed.Exception is { } ex)
        {
            log.LogWarning(ex, "The library search failed");
            executed.ExceptionHandled = true;
            return (executed, [], 0);
        }

        if (
            executed.Result is ObjectResult { Value: QueryResult<BaseItemDto> local }
            && local.Items is { } items
        )
        {
            // A full answer is no total: Jellyfin 12.1 asks its search providers for three times
            // the limit and counts what they returned, so the total grew with the page (30 for a
            // first page of 10, 60 for the second) and the two pages of one search disagreed.
            // A client that asked for no total (the web client's search) pays nothing for it.
            ctx.TryGetActionArgument("enableTotalRecordCount", out var wantsTotal, true);
            var total =
                items.Count < upTo || !wantsTotal
                    ? local.TotalRecordCount
                    : Math.Max(local.TotalRecordCount, await CountLibraryMatchesAsync(ctx));
            return (executed, items, total);
        }

        return (executed, [], 0);
    }

    private const int MaxCountedLibraryMatches = 5000;

    /// <summary>
    /// How many items the library search matches for this request, whatever the page: the
    /// providers' hits without a limit, counted under the user and the scope the request has.
    /// </summary>
    private async Task<int> CountLibraryMatchesAsync(ActionExecutingContext ctx)
    {
        ctx.TryGetUserId(out var userId);
        ctx.TryGetActionArgument<string>("searchTerm", out var searchTerm);
        ctx.TryGetActionArgument<BaseItemKind[]>("includeItemTypes", out var include, []);
        ctx.TryGetActionArgument<BaseItemKind[]>("excludeItemTypes", out var exclude, []);
        ctx.TryGetActionArgument<MediaType[]>("mediaTypes", out var mediaTypes, []);
        ctx.TryGetActionArgument<Guid?>("parentId", out var parentId);
        if (string.IsNullOrWhiteSpace(searchTerm))
            return 0;

        var hits = await searchManager
            .GetSearchResultsAsync(
                new SearchProviderQuery
                {
                    SearchTerm = searchTerm,
                    UserId = userId.Equals(Guid.Empty) ? null : userId,
                    IncludeItemTypes = include,
                    ExcludeItemTypes = exclude,
                    MediaTypes = mediaTypes,
                    ParentId = parentId,
                    // Without a limit the providers stop at 100. A fixed cap keeps the count the
                    // same for every page; a term matching more than this is no page anyone reaches.
                    Limit = MaxCountedLibraryMatches,
                },
                ctx.HttpContext.RequestAborted
            )
            .ConfigureAwait(false);
        if (hits.Count == 0)
            return 0;

        return libraryManager.GetCount(
            new InternalItemsQuery(userManager.GetUserById(userId))
            {
                ItemIds = hits.Select(h => h.ItemId).ToArray(),
                IncludeItemTypes = include,
                ExcludeItemTypes = exclude,
                MediaTypes = mediaTypes,
                ParentId = parentId ?? Guid.Empty,
                Recursive = true,
            }
        );
    }

    /// <summary>
    /// Drops the types whose Gelato folder is not inside the library the request is scoped to, so
    /// a search inside one library is not answered with another library's titles.
    /// </summary>
    /// <remarks>
    /// A client searching inside a library sends it as parentId: Jellyfin scopes its own half to
    /// it, and the addon's half has to be scoped the same way or the library is filled with
    /// titles it does not hold — and with the items those results stand in for, which belong to
    /// the Gelato library. A search that names no parent is scoped to nothing and keeps both
    /// types. topParentId is not a parameter of the endpoint (it is the web client's route, not
    /// its query), so it scopes nothing here either.
    /// </remarks>
    private void LimitToScope(
        ActionExecutingContext ctx,
        PluginConfiguration cfg,
        Guid userId,
        HashSet<BaseItemKind> requestedTypes
    )
    {
        if (
            !ctx.TryGetActionArgument<Guid>("parentId", out var scope)
            || scope.Equals(Guid.Empty)
            || requestedTypes.Count == 0
        )
        {
            return;
        }

        if (
            requestedTypes.Contains(BaseItemKind.Movie)
            && !manager.IsWithinScope(scope, cfg.MovieFolder ?? manager.TryGetMovieFolder(userId))
        )
        {
            requestedTypes.Remove(BaseItemKind.Movie);
        }

        if (
            requestedTypes.Contains(BaseItemKind.Series)
            && !manager.IsWithinScope(scope, cfg.SeriesFolder ?? manager.TryGetSeriesFolder(userId))
        )
        {
            requestedTypes.Remove(BaseItemKind.Series);
        }

        if (requestedTypes.Count == 0)
        {
            log.LogDebug(
                "The search is scoped to {Scope}, which holds neither Gelato folder: the library answers it alone",
                scope
            );
        }
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
    /// Every field but the two extras counts. Each of those runs a library query for the item's
    /// extras, about 45 ms apiece, and a search answers with 40 results: 3.5 s of a 4.3 s search
    /// went into counting trailers and special features, which a placeholder item never has and
    /// a search result never shows.
    /// </summary>
    private static readonly ItemFields[] SearchResultFields = Enum.GetValues<ItemFields>()
        .Where(f => f is not (ItemFields.LocalTrailerCount or ItemFields.SpecialFeatureCount))
        .ToArray();

    /// <summary>
    /// The addon's results as DTOs, and, per result, the library item it stands in for when the
    /// library has the title already.
    /// </summary>
    private async Task<(List<BaseItemDto> Dtos, HashSet<Guid> Covered)> ConvertMetasToDtos(
        List<StremioMeta> metas,
        Guid userId,
        ItemFields[] fields,
        CancellationToken ct
    )
    {
        // theres a reason i initally disabled all fields but forgot....
        // infuse breaks if we do a small subset. Not sure which field it needs. Prolly mediasources
        var options = new DtoOptions(false)
        {
            Fields = SearchResultFields,
            EnableImages = true,
            EnableUserData = false,
        };

        // The library's own item is answered with its user data: what the grid draws a watched
        // tick and a resume bar from. A stand-in has none to read — the id is a title the library
        // does not hold — which is why the results the addon answers for alone keep it off.
        // It carries the fields the client asked for, as Jellyfin's own search does: all of them
        // cost a series its season and episode counts and a movie its cast and streams, per
        // result, for a grid that shows a poster and a title.
        var libraryOptions = new DtoOptions(false)
        {
            Fields = fields,
            EnableImages = true,
            EnableUserData = true,
        };
        var user = userManager.GetUserById(userId);

        var results = metas
            .Select(meta => (Meta: meta, Item: manager.IntoBaseItem(meta)))
            .Where(r => r.Item is not null)
            .Select(r => (r.Meta, Item: r.Item!))
            .ToList();
        var libraryItems = await FindLibraryItemsAsync(results.Select(r => r.Item), user, ct);

        var dtos = new List<BaseItemDto>(metas.Count);

        // The movie and series catalogs are searched separately and their results concatenated,
        // but an addon may return the same title under both — a series showing up in the movie
        // results, say. The ids are deterministic, so the same title yields the same id twice
        // and the client renders it twice. Keep the first occurrence and drop later repeats.
        var seen = new HashSet<Guid>();
        var covered = new HashSet<Guid>();

        foreach (var (meta, baseItem) in results)
        {
            var stremioUri = StremioUri.FromBaseItem(baseItem);
            var searchId = stremioUri.ToGuid();

            if (!seen.Add(searchId))
                continue;

            // The library's own item for this title, matched from the same base item the DTO
            // would be built from. It answers in the result's place; the library half leaves it
            // out. Two results of one title — an addon that carries it under a tmdb: id and a tt
            // one — resolve to the same item, and only the first takes it, so the answer holds no
            // id twice.
            var existing = FindMatch(libraryItems, baseItem);
            var dto =
                existing is not null && covered.Add(existing.Id)
                    ? dtoService.GetBaseItemDto(existing, libraryOptions, user)
                    : dtoService.GetBaseItemDto(baseItem, options);

            if (dto.Id != existing?.Id)
                dto.Id = searchId;

            dtos.Add(dto);

            // Kept under the id the result would have carried either way: a client that opened
            // this title before the library had it holds that id in its page URL, and the reads
            // it issues with it are resolved through the meta saved here.
            manager.SaveStremioMeta(searchId, meta);
        }

        return (dtos, covered);
    }

    /// <summary>
    /// The library items any of the results stands in for, in two queries for the whole search:
    /// the items holding one of the results' provider ids, then those items. Asking
    /// <see cref="GelatoManager.FindExistingItem"/> per result cost a query each, about 16 ms, and
    /// most results are not in the library, so the miss has to be the cheap case.
    /// </summary>
    private async Task<IReadOnlyList<BaseItem>> FindLibraryItemsAsync(
        IEnumerable<BaseItem> candidates,
        User? user,
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
            // Names and values are matched separately, so a pair from two different results
            // can match too; FindMatch sorts that out on the loaded items.
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
}
