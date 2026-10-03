using System.Runtime.ExceptionServices;
using Gelato.Config;
using Gelato.Services;
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
    LibraryFolderService libraryFolders,
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
        ctx.TryGetActionArgument<Guid>("parentId", out var scope);
        var folders = LimitToScope(scope, userId, requestedTypes);
        if (requestedTypes.Count == 0)
        {
            await next();
            return;
        }

        ctx.TryGetActionArgument("startIndex", out var start, 0);
        ctx.TryGetActionArgument("limit", out var limit, 25);

        // The catalogs are asked first and awaited after Jellyfin's own search has run: neither
        // half needs the other's answer, the library's takes 45 to 70 ms and the addon's 25 ms
        // to two seconds, so one after the other a search waited for both.
        var addon = SearchMetasAsync(searchTerm, requestedTypes, cfg, stremio, userId, folders);

        // A client asks for every type it wants in one request: the web client's global search
        // sends Movie, Series, Episode, BoxSet, TvChannel and more together. Answering all of it
        // with the addon's movies and series dropped the rest, so a channel was only ever found
        // inside Live TV, where the client asks for TvChannel alone and the search never gets
        // this far (lostb1t/Gelato#162). Let Jellyfin answer for everything it holds and put its
        // results after the addon's.
        ActionExecutedContext? executed;
        IReadOnlyList<BaseItemDto> localItems;
        int localTotal;
        bool cut;
        try
        {
            (executed, localItems, localTotal, cut) = await SearchLibraryAsync(
                ctx,
                next,
                start + limit
            );
        }
        catch
        {
            // Nobody awaits the addon's answer any more. Its failure was logged where it
            // happened; looking at it here keeps it from surfacing as an unobserved exception.
            _ = addon.ContinueWith(
                static t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted
            );
            throw;
        }

        // Fails the request when no catalog answered, as before: after the library's half now,
        // whose answer is dropped with it.
        var metas = await addon;

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
            scope,
            folders,
            ctx.HttpContext.RequestAborted
        );
        var libraryItems = localItems.Where(i => !covered.Contains(i.Id)).ToArray();
        var paged = dtos.Concat(libraryItems).Skip(start).Take(limit).ToArray();

        // What the library adds behind the addon's results. An answer that was not cut at the end
        // of the page is all of it. One that was cut is counted through the query the list itself
        // comes from, the owned titles left out, so every page of a search reports the total its
        // last page ends at, which is what a client pages by. A client that asked for no total
        // (the web client's search) pays for no count and gets the estimate from Jellyfin's own
        // number, which grows with the page and counts owned titles the library's matches may
        // not hold.
        var library = libraryItems.Length;
        if (cut)
        {
            ctx.TryGetActionArgument("enableTotalRecordCount", out var wantsTotal, true);
            library = Math.Max(
                library,
                wantsTotal
                    ? await CountLibraryMatchesAsync(ctx, covered)
                    : localTotal - covered.Count
            );
        }

        var total = dtos.Count + library;

        // addon: what the addon's half answers with, after invalid and duplicate results are
        // dropped; owned: of those, titles the library already has; library: what the library
        // adds to them.
        log.LogInformation(
            "Intercepted /Items search \"{Query}\" types=[{Types}] start={Start} limit={Limit} addon={Addon} owned={Owned} library={Library} returned={Returned} total={Total}",
            searchTerm,
            string.Join(",", requestedTypes),
            start,
            limit,
            dtos.Count,
            covered.Count,
            library,
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
    /// items, its total and whether the answer was cut at <paramref name="upTo"/>. It answers for
    /// every type, movies and series included: the library's own copy of a title the addon
    /// answered for is taken out of the concatenation afterwards, which leaves the files the
    /// library holds that no catalog carries findable.
    /// </summary>
    private async Task<(
        ActionExecutedContext? Executed,
        IReadOnlyList<BaseItemDto> Items,
        int Total,
        bool Cut
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
            return (executed, [], 0, false);
        }

        if (
            executed.Result is ObjectResult { Value: QueryResult<BaseItemDto> local }
            && local.Items is { } items
        )
        {
            // The total of an answer that was cut is no total: Jellyfin 12.1 asks its search
            // providers for three times the limit and counts what they returned, so it grew with
            // the page (30 for a first page of 10, 60 for the second).
            return (executed, items, local.TotalRecordCount, items.Count >= upTo);
        }

        return (executed, [], 0, false);
    }

    private const int MaxCountedLibraryMatches = 5000;

    /// <summary>
    /// How many items the library search lists for this request behind the addon's results,
    /// whatever the page: the providers' hits without a limit, under the user and the scope the
    /// request has, without the items in <paramref name="covered"/>.
    /// </summary>
    private async Task<int> CountLibraryMatchesAsync(
        ActionExecutingContext ctx,
        IReadOnlyCollection<Guid> covered
    )
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

        // Counted by the query Jellyfin's search lists with. GetCount is another one: it goes
        // past the listing filters, so it counted the unreleased titles a list never shows, and a
        // cut page reported more than the page that held the end of the list ("day": 46 on the
        // first page of 10, 44 on the second).
        return libraryManager
            .GetItemsResult(
                new InternalItemsQuery(userManager.GetUserById(userId))
                {
                    ItemIds = hits.Select(h => h.ItemId).ToArray(),
                    ExcludeItemIds = [.. covered],
                    IncludeItemTypes = include,
                    ExcludeItemTypes = exclude,
                    MediaTypes = mediaTypes,
                    ParentId = parentId ?? Guid.Empty,
                    Recursive = true,
                    // The count alone, no rows.
                    Limit = 0,
                }
            )
            .TotalRecordCount;
    }

    /// <summary>
    /// Drops the types no Gelato folder inside the library the request is scoped to takes, so a
    /// search inside one library is not answered with another library's titles. Returns, per type
    /// that stays, the folder a result opened from this search goes into when that is not the
    /// user's own movie or series folder: Gelato's folder in the library that was searched,
    /// whether a catalog's, another user's or the default one. A library the user cannot open is
    /// left to Jellyfin altogether, or the user could fill it by searching inside it.
    /// </summary>
    /// <remarks>
    /// A client searching inside a library sends it as parentId: Jellyfin scopes its own half to
    /// it, and the addon's half has to be scoped the same way or the library is filled with
    /// titles it does not hold — and with the items those results stand in for, which belong to
    /// the Gelato library. A search that names no parent is scoped to nothing and keeps both
    /// types. topParentId is not a parameter of the endpoint (it is the web client's route, not
    /// its query), so it scopes nothing here either.
    /// </remarks>
    private Dictionary<BaseItemKind, Folder> LimitToScope(
        Guid scope,
        Guid userId,
        HashSet<BaseItemKind> requestedTypes
    )
    {
        var folders = new Dictionary<BaseItemKind, Folder>();
        if (scope.Equals(Guid.Empty) || requestedTypes.Count == 0)
        {
            return folders;
        }

        if (
            userManager.GetUserById(userId) is { } user
            && libraryManager.GetItemById(scope) is { } scoped
            && !scoped.IsVisibleStandalone(user)
        )
        {
            log.LogDebug(
                "The search is scoped to {Scope}, which the user cannot open: the library answers it alone",
                scope
            );
            requestedTypes.Clear();
            return folders;
        }

        foreach (var kind in new[] { BaseItemKind.Movie, BaseItemKind.Series })
        {
            if (!requestedTypes.Contains(kind))
                continue;

            var own =
                kind == BaseItemKind.Series
                    ? manager.TryGetSeriesFolder(userId)
                    : manager.TryGetMovieFolder(userId);
            var folder =
                manager.GetSearchFolder(scope, userId, kind) ?? GetLibraryFolder(scope, kind);
            if (folder is null)
                requestedTypes.Remove(kind);
            else if (folder.Id != own?.Id)
                folders[kind] = folder;
        }

        if (requestedTypes.Count == 0)
        {
            log.LogDebug(
                "The search is scoped to {Scope}, which holds no Gelato folder for what it asks: the library answers it alone",
                scope
            );
        }

        return folders;
    }

    /// <summary>
    /// Gelato's folder in the library the search is scoped to, when it is neither the user's own
    /// nor a catalog's: the default folder or another user's, or one nothing is configured on
    /// any more. A library that was picked once keeps the folder, and a search inside it is
    /// still answered and its results still go there.
    /// </summary>
    private Folder? GetLibraryFolder(Guid scope, BaseItemKind kind)
    {
        var path = libraryFolders.GetLibrary(scope)?.GelatoPath;
        var folder = path is null ? null : manager.TryGetLibraryFolder(path);
        return folder is not null && manager.FolderTakes(folder, kind) ? folder : null;
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
        Guid userId,
        Dictionary<BaseItemKind, Folder> folders
    )
    {
        var tasks = new List<(StremioMediaType Type, Task<IReadOnlyList<StremioMeta>> Task)>();

        // Where a result of this search goes when it is opened: the folder of the library that
        // was searched, else the user's movie or series folder. Without either there is nowhere
        // to put it.
        var movieFolder =
            folders.GetValueOrDefault(BaseItemKind.Movie)
            ?? cfg.MovieFolder
            ?? manager.TryGetMovieFolder(userId);
        var seriesFolder =
            folders.GetValueOrDefault(BaseItemKind.Series)
            ?? cfg.SeriesFolder
            ?? manager.TryGetSeriesFolder(userId);

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
    /// The fields of a result whose id the library does not know (<see cref="FindUnknownIdsAsync"/>).
    /// The four left out are read from the database by the item's id, so they can only come back
    /// empty for it, and <see cref="GetUnknownItemDto"/> sets them to what the full build answers:
    /// no people, no chapters, no trickplay (the manifest skips a remote source, and builds the
    /// item's sources a second time to find that out) and no media source count (it is only
    /// sent when it is not one). A search of 39 such results ran 274 queries, seven per result.
    /// </summary>
    private static readonly ItemFields[] UnknownItemFields = SearchResultFields
        .Where(f =>
            f
                is not (
                    ItemFields.People
                    or ItemFields.Chapters
                    or ItemFields.Trickplay
                    or ItemFields.MediaSourceCount
                )
        )
        .ToArray();

    /// <summary>
    /// The addon's results as DTOs, and, per result, the library item it stands in for when the
    /// library has the title already.
    /// </summary>
    private async Task<(List<BaseItemDto> Dtos, HashSet<Guid> Covered)> ConvertMetasToDtos(
        List<StremioMeta> metas,
        Guid userId,
        ItemFields[] fields,
        Guid scope,
        Dictionary<BaseItemKind, Folder> folders,
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
        var unknownOptions = new DtoOptions(false)
        {
            Fields = UnknownItemFields,
            EnableImages = true,
            EnableUserData = false,
        };
        var user = userManager.GetUserById(userId);

        var results = metas
            .Select(meta => (Meta: meta, Item: manager.IntoBaseItem(meta)))
            .Where(r => r.Item is not null)
            .Select(r => (r.Meta, Item: r.Item!))
            .ToList();
        var libraryItems = await FindLibraryItemsAsync(results.Select(r => r.Item), user, ct);
        var unknownIds = await FindUnknownIdsAsync(results.Select(r => r.Item.Id), ct);

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

            // A title another library holds is not a result of a search inside this one: the
            // item is not in the scope. A stand-in in its place would not put the title here
            // either, opening it redirects to the item the library has. For a Gelato item that
            // cannot be otherwise (its id is the title's, there is no second one); a second,
            // Gelato copy of a title held as a file would be decided here and in the insert.
            if (existing is not null && !manager.IsWithinScope(scope, existing))
                continue;

            var dto =
                existing is not null && covered.Add(existing.Id)
                    ? dtoService.GetBaseItemDto(existing, libraryOptions, user)
                : unknownIds.Contains(baseItem.Id) ? GetUnknownItemDto(baseItem, unknownOptions)
                : dtoService.GetBaseItemDto(baseItem, options);

            if (dto.Id != existing?.Id)
                dto.Id = searchId;

            dtos.Add(dto);

            // Kept under the id the result would have carried either way: a client that opened
            // this title before the library had it holds that id in its page URL, and the reads
            // it issues with it are resolved through the meta saved here.
            manager.SaveStremioMeta(searchId, meta);

            // Opening the result puts the title into the library that was searched; a search of
            // everything, or of the library the user's own folder is in, clears it again.
            manager.RememberSearchFolder(
                userId,
                searchId,
                folders.GetValueOrDefault(baseItem.GetBaseItemKind())
            );
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

    /// <summary>
    /// The DTO of a result whose id the library does not know, as the full build answers it,
    /// without the queries that can only come back empty. See <see cref="UnknownItemFields"/>.
    /// </summary>
    private BaseItemDto GetUnknownItemDto(BaseItem item, DtoOptions options)
    {
        item.MarkNotInLibrary();
        var dto = dtoService.GetBaseItemDto(item, options);
        dto.People = [];
        dto.Chapters = [];
        if (item is Video)
            dto.Trickplay = new();
        return dto;
    }

    /// <summary>
    /// Of the ids the results' items are built with, those no item of the library has: neither
    /// as its own id nor as the item it is a version or an extra of. One query for the whole
    /// search. An id it does not return is known to some row, and that result is built in full:
    /// a title the library holds but this user may not see has the id its result is built with,
    /// and so can a row whose movie is gone.
    /// </summary>
    private async Task<HashSet<Guid>> FindUnknownIdsAsync(
        IEnumerable<Guid> candidates,
        CancellationToken ct
    )
    {
        var unknown = candidates.ToHashSet();
        if (unknown.Count == 0)
            return unknown;

        var ids = unknown.Select(id => (Guid?)id).ToArray();
        var db = await dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using (db.ConfigureAwait(false))
        {
            var known = await db
                .BaseItems.AsNoTracking()
                .Where(b =>
                    ids.Contains(b.Id)
                    || ids.Contains(b.PrimaryVersionId)
                    || ids.Contains(b.OwnerId)
                )
                .Select(b => new
                {
                    b.Id,
                    b.PrimaryVersionId,
                    b.OwnerId,
                })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var row in known)
            {
                unknown.Remove(row.Id);
                if (row.PrimaryVersionId is { } primary)
                    unknown.Remove(primary);
                if (row.OwnerId is { } owner)
                    unknown.Remove(owner);
            }
        }

        return unknown;
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
