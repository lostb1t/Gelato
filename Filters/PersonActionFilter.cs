using System.Globalization;
using Gelato.Config;
using Gelato.Decorators;
using Gelato.Services;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

/// <summary>
/// With <see cref="PluginConfiguration.PersonRemoteTitles"/> on, a person's page also lists the
/// person's films and series the library does not hold, from TMDB's credits of the person, as
/// stand-ins that open like search results. Only the request a person's page sends is answered:
/// <c>/Items</c> with one person id, recursive, sorted by a key a stand-in has, and narrowed by
/// nothing else. Any other request, and every request while the setting is off, is Jellyfin's.
/// </summary>
public class PersonActionFilter(
    IDtoService dtoService,
    IUserManager userManager,
    ILibraryManager libraryManager,
    IItemRepository itemRepository,
    IServerConfigurationManager serverConfig,
    GelatoManager manager,
    SearchActionFilter search,
    ParentalRatings parental,
    ILogger<PersonActionFilter> log
) : IAsyncActionFilter
{
    /// <summary>
    /// What a person's page sends besides the person, in the web client, Android TV, Swiftfin
    /// and Findroid. A request with any other parameter set (a filter, a genre, a search term, a
    /// parent) asks for something the person's credits cannot answer for.
    /// </summary>
    private static readonly HashSet<string> PersonPageParameters = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        "userId",
        "personIds",
        "includeItemTypes",
        "excludeItemTypes",
        "mediaTypes",
        "recursive",
        "sortBy",
        "sortOrder",
        "startIndex",
        "limit",
        "fields",
        "enableImages",
        "imageTypeLimit",
        "enableImageTypes",
        "enableUserData",
        "enableTotalRecordCount",
        "collapseBoxSetItems",
        "api_key",
        "ApiKey",
    };

    /// <summary>The sort keys a stand-in has a value for.</summary>
    private static readonly HashSet<ItemSortBy> SortableBy =
    [
        ItemSortBy.SortName,
        ItemSortBy.Name,
        ItemSortBy.PremiereDate,
        ItemSortBy.ProductionYear,
        ItemSortBy.CommunityRating,
    ];

    /// <summary>TMDB's talk show, news and reality genres: appearances, not roles.</summary>
    private static readonly HashSet<int> NoiseGenres = [10767, 10763, 10764];

    /// <summary>The crew jobs that make a title the person's: directing, writing, creating.</summary>
    private static readonly HashSet<string> CrewJobs = new(StringComparer.OrdinalIgnoreCase)
    {
        "Director",
        "Writer",
        "Screenplay",
        "Story",
        "Novel",
        "Teleplay",
        "Author",
        "Creator",
    };

    /// <summary>How long a page waits for TMDB release lookups and imdb ids of its stand-ins.</summary>
    private static readonly TimeSpan LookupBudget = TimeSpan.FromSeconds(3);

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        ctx.TryGetUserId(out var userId);
        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        if (
            !cfg.PersonRemoteTitles
            || cfg.DisableSearch
            || cfg.Stremio is not { } stremio
            || ctx.GetActionName() is not { } action
        )
        {
            await next();
            return;
        }

        if (
            action.Equals("GetItem", StringComparison.OrdinalIgnoreCase)
            || action.Equals("GetItemLegacy", StringComparison.OrdinalIgnoreCase)
        )
        {
            await CountRemoteTitlesAsync(ctx, next, cfg, stremio, userId);
            return;
        }

        if (
            !ctx.IsGetItemsAction()
            || GetPersonPage(ctx) is not { } page
            || libraryManager.GetItemById(page.PersonId) is not Person person
            || person.GetProviderId(MetadataProvider.Tmdb) is not { Length: > 0 }
            || !await stremio.IsReady()
        )
        {
            await next();
            return;
        }

        await ListAsync(ctx, next, cfg, stremio, userId, person, page);
    }

    private sealed record PersonPage(
        Guid PersonId,
        HashSet<BaseItemKind> Kinds,
        ItemSortBy[] SortBy,
        SortOrder[] SortOrder
    );

    /// <summary>
    /// A credit outside the person's own library listing: a stand-in, or, in <see cref="Library"/>,
    /// the library's item of a title that does not list the person (one just opened from this
    /// page, whose people are not refreshed yet).
    /// </summary>
    private sealed record RemoteTitle(
        StremioMeta Meta,
        BaseItem Item,
        double Popularity,
        BaseItem? Library = null
    )
    {
        public BaseItem SortItem => Library ?? Item;
    }

    /// <summary>
    /// The person and the types a request asks for when it is a person's page, else null.
    /// </summary>
    private static PersonPage? GetPersonPage(ActionExecutingContext ctx)
    {
        foreach (var (key, value) in ctx.HttpContext.Request.Query)
        {
            if (!PersonPageParameters.Contains(key) && value.Any(v => !string.IsNullOrEmpty(v)))
                return null;
        }

        if (
            !ctx.TryGetActionArgument<Guid[]>("personIds", out var personIds)
            || personIds is not { Length: 1 }
            || !ctx.TryGetActionArgument<bool?>("recursive", out var recursive)
            || recursive != true
        )
            return null;

        ctx.TryGetActionArgument<ItemSortBy[]>("sortBy", out var sortBy, []);
        if (sortBy.Any(s => !SortableBy.Contains(s)))
            return null;
        ctx.TryGetActionArgument<SortOrder[]>("sortOrder", out var sortOrder, []);

        var kinds = new HashSet<BaseItemKind>([BaseItemKind.Movie, BaseItemKind.Series]);
        if (
            ctx.TryGetActionArgument<BaseItemKind[]>("includeItemTypes", out var include)
            && include is { Length: > 0 }
        )
            kinds.IntersectWith(include);
        if (ctx.TryGetActionArgument<BaseItemKind[]>("excludeItemTypes", out var exclude))
            kinds.ExceptWith(exclude ?? []);
        if (ctx.TryGetActionArgument<MediaType[]>("mediaTypes", out var mediaTypes))
        {
            if (mediaTypes is { Length: > 0 } && !mediaTypes.Contains(MediaType.Video))
                kinds.Clear();
            else if (mediaTypes?.Contains(MediaType.Video) == true)
                kinds.Remove(BaseItemKind.Series);
        }

        return kinds.Count == 0 ? null : new PersonPage(personIds[0], kinds, sortBy, sortOrder);
    }

    /// <summary>
    /// The person's page: the library's own answer, with the person's titles it does not hold
    /// merged in by the request's sort, or behind it by TMDB popularity when the request names
    /// none. Paged and counted over both.
    /// </summary>
    private async Task ListAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next,
        PluginConfiguration cfg,
        GelatoStremioProvider stremio,
        Guid userId,
        Person person,
        PersonPage page
    )
    {
        var ct = ctx.HttpContext.RequestAborted;
        var user = userManager.GetUserById(userId);
        // Before the library's own answer, not beside it: a title opened from this page gets the
        // person with the refresh after its insert. Asked whether it lists the person after the
        // library had answered, it could be left out of both halves.
        List<RemoteTitle> remote;
        try
        {
            remote = await GetRemoteTitlesAsync(cfg, stremio, userId, user, person, page.Kinds, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Person {Name}: the titles outside the library failed", person.Name);
            remote = [];
        }

        ctx.TryGetActionArgument("startIndex", out int? startArg);
        ctx.TryGetActionArgument("limit", out int? limitArg);
        var start = Math.Max(startArg ?? 0, 0);

        // Jellyfin pages its own answer, so ask it for everything up to the end of the page and
        // page the merge here.
        ctx.ActionArguments["startIndex"] = 0;
        ctx.ActionArguments["limit"] = limitArg is { } l ? start + l : null;
        var executed = await next();
        if (
            executed.Exception is not null
            || executed.Result is not ObjectResult { Value: QueryResult<BaseItemDto> local }
        )
            return;

        // The other way round, a title whose people arrived in between is in both: the library's
        // answer keeps it.
        var localItems = local.Items ?? [];
        var localIds = localItems.Select(i => i.Id).ToHashSet();
        remote = remote.Where(r => r.Library is null || !localIds.Contains(r.Library.Id)).ToList();
        var merged = Merge(localItems, remote, page);
        var end = limitArg is { } limit ? start + limit : int.MaxValue;
        var slice = merged.Skip(start).Take(end - start).ToList();
        ctx.TryGetActionArgument<ItemFields[]>("fields", out var fields, []);
        var dtos = await BuildPageAsync(slice, fields, stremio, userId, user, ct);

        var total = Math.Max(local.TotalRecordCount, localItems.Count) + remote.Count;
        log.LogInformation(
            "Person {Name}: types=[{Types}] start={Start} limit={Limit} library={Library} remote={Remote} returned={Returned} total={Total}",
            person.Name,
            string.Join(",", page.Kinds),
            start,
            limitArg,
            local.TotalRecordCount,
            remote.Count,
            dtos.Count,
            total
        );

        executed.Result = new OkObjectResult(
            new QueryResult<BaseItemDto>
            {
                StartIndex = start,
                Items = dtos,
                TotalRecordCount = total,
            }
        );
    }

    /// <summary>
    /// A person's item answered with the counts of their titles outside the library added: the
    /// web client draws a person's Movies and Shows sections only for a type it counts.
    /// </summary>
    private async Task CountRemoteTitlesAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next,
        PluginConfiguration cfg,
        GelatoStremioProvider stremio,
        Guid userId
    )
    {
        var executed = await next();
        if (
            executed.Exception is not null
            || executed.Result is not ObjectResult { Value: BaseItemDto dto }
            || dto.Type != BaseItemKind.Person
            || (dto.MovieCount is null && dto.SeriesCount is null)
            || libraryManager.GetItemById(dto.Id) is not Person person
            || person.GetProviderId(MetadataProvider.Tmdb) is not { Length: > 0 }
            || !await stremio.IsReady()
        )
            return;

        try
        {
            var remote = await GetRemoteTitlesAsync(
                cfg,
                stremio,
                userId,
                userManager.GetUserById(userId),
                person,
                [BaseItemKind.Movie, BaseItemKind.Series],
                ctx.HttpContext.RequestAborted
            );
            dto.MovieCount =
                (dto.MovieCount ?? 0) + remote.Count(r => r.Item is Movie);
            dto.SeriesCount =
                (dto.SeriesCount ?? 0) + remote.Count(r => r.Item is Series);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Person {Name}: counting the titles outside the library failed", person.Name);
        }
    }

    /// <summary>
    /// The person's credited films and series of <paramref name="kinds"/> the library does not
    /// hold, by TMDB popularity: without appearances as themselves, talk, news and reality shows,
    /// crew credits other than directing and writing, and, with the unreleased filter on, what is
    /// not released yet. A title the library holds is left out, also where this user cannot see
    /// it or the listing hides it: it is the library's to show.
    /// </summary>
    private async Task<List<RemoteTitle>> GetRemoteTitlesAsync(
        PluginConfiguration cfg,
        GelatoStremioProvider stremio,
        Guid userId,
        User? user,
        Person person,
        HashSet<BaseItemKind> kinds,
        CancellationToken ct
    )
    {
        var tmdbId = person.GetProviderId(MetadataProvider.Tmdb)!;
        var credits = await stremio
            .GetPersonCreditsAsync(tmdbId, MetadataLanguage(), ct)
            .ConfigureAwait(false);
        if (credits is null)
            return [];

        // Where an opened title goes; without a folder for a type there is nowhere to put it.
        if ((cfg.MovieFolder ?? manager.TryGetMovieFolder(userId)) is null)
            kinds = [.. kinds.Where(k => k != BaseItemKind.Movie)];
        if ((cfg.SeriesFolder ?? manager.TryGetSeriesFolder(userId)) is null)
            kinds = [.. kinds.Where(k => k != BaseItemKind.Series)];
        if (kinds.Count == 0)
            return [];

        var metas = Filmography(credits, person.Name)
            .Select(c => (Credit: c, Meta: ToMeta(c)))
            .Where(c => c.Meta is not null && kinds.Contains(c.Meta.Type == StremioMediaType.Movie ? BaseItemKind.Movie : BaseItemKind.Series))
            .Select(c => (c.Credit, Meta: c.Meta!))
            .ToList();

        if (cfg.FilterUnreleased)
        {
            // As search does: a movie premiered within the last year is judged by TMDB's digital
            // release date, memoised per movie. One that does not answer in time stays hidden.
            var lookups = metas.Where(m => m.Meta.NeedsReleaseLookup()).ToList();
            if (lookups.Count > 0)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(LookupBudget);
                try
                {
                    await Parallel
                        .ForEachAsync(
                            lookups,
                            new ParallelOptions
                            {
                                MaxDegreeOfParallelism = 4,
                                CancellationToken = cts.Token,
                            },
                            async (m, t) =>
                                await stremio
                                    .EnrichDigitalReleaseDateAsync(m.Meta, t)
                                    .ConfigureAwait(false)
                        )
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    log.LogDebug("Person {Name}: TMDB release lookups ran out of time", person.Name);
                }
            }

            metas = metas.Where(m => m.Meta.IsReleased(cfg.FilterUnreleasedBufferDays)).ToList();
        }

        // As search does: what the user's parental control would hide in the library.
        if (ParentalRatings.Restricts(user))
        {
            var allowed = (
                await parental
                    .FilterAsync(
                        [.. metas.Select(m => m.Meta)],
                        user,
                        type =>
                            type == StremioMediaType.Series
                                ? cfg.SeriesFolder ?? manager.TryGetSeriesFolder(userId)
                                : cfg.MovieFolder ?? manager.TryGetMovieFolder(userId),
                        stremio,
                        ct
                    )
                    .ConfigureAwait(false)
            ).ToHashSet(ReferenceEqualityComparer.Instance);
            metas = metas.Where(m => allowed.Contains(m.Meta)).ToList();
        }

        var titles = metas
            .Select(m => (m.Credit, m.Meta, Item: manager.IntoBaseItem(m.Meta)))
            .Where(t => t.Item is not null)
            .Select(t => new RemoteTitle(t.Meta, t.Item!, t.Credit.Popularity))
            .ToList();

        var libraryItems = await search
            .FindLibraryItemsAsync(titles.Select(t => t.Item), ct)
            .ConfigureAwait(false);

        // A title the library holds is the library's to list. Where its item lists the person, the
        // library's own answer has it. One that does not (opened from this page a moment ago, its
        // people not refreshed yet, or a cast that left the person out) is listed here as that
        // item, as search answers with it, unless the user cannot see it or the unreleased filter
        // hides it from listings.
        var result = new List<RemoteTitle>(titles.Count);
        var seen = new HashSet<Guid>();
        foreach (var title in titles)
        {
            // A stand-in opened a moment ago is that item: matched by the id it was opened as,
            // since its TMDB id only reaches the item with the refresh that follows the insert.
            var matches = SearchActionFilter.FindMatches(libraryItems, title.Item);
            if (
                matches.Count == 0
                && manager.GetInsertedId(StremioUri.FromBaseItem(title.Item)!.ToGuid()) is { } opened
                && libraryManager.GetItemById(opened) is { } openedItem
            )
                matches = [openedItem];
            if (matches.Count == 0)
            {
                if (seen.Add(title.Item.Id))
                    result.Add(title);
                continue;
            }

            var visible = matches.FirstOrDefault(m => user is null || m.IsVisibleStandalone(user));
            if (visible is not null && seen.Add(visible.Id))
                result.Add(title with { Library = visible });
        }

        var held = result.Where(r => r.Library is not null).Select(r => r.Library!.Id).ToArray();
        if (held.Length == 0)
            return result;

        var listed = libraryManager
            .GetItemList(
                new InternalItemsQuery(user)
                {
                    PersonIds = [person.Id],
                    ItemIds = held,
                    Recursive = true,
                }
            )
            .Select(i => i.Id)
            .ToHashSet();
        if (
            cfg.FilterUnreleased
            && itemRepository is GelatoItemRepository repository
            && repository.GetUnreleasedIds(cfg.FilterUnreleasedBufferDays) is { Count: > 0 } hidden
        )
            listed.UnionWith(hidden);

        return result.Where(r => r.Library is null || !listed.Contains(r.Library.Id)).ToList();
    }

    /// <summary>
    /// The server's metadata language, as TMDB takes it ("de-DE"): the titles the library's own
    /// items are named in.
    /// </summary>
    private string? MetadataLanguage()
    {
        var language = serverConfig.Configuration.PreferredMetadataLanguage;
        var country = serverConfig.Configuration.MetadataCountryCode;
        if (string.IsNullOrWhiteSpace(language))
            return null;
        return string.IsNullOrWhiteSpace(country) || language.Contains('-')
            ? language
            : $"{language}-{country}";
    }

    /// <summary>
    /// The person's own titles among their TMDB credits, most popular first, each once: roles
    /// (not appearances as themselves, archive footage, talk, news or reality shows) and the
    /// directing, writing and creating crew credits. Producer credits are left out.
    /// </summary>
    internal static List<TmdbPersonCredit> Filmography(TmdbPersonCredits credits, string? personName)
    {
        var cast = credits.Cast.Where(c => !IsAppearance(c.Character, personName));
        var crew = credits.Crew.Where(c => c.Job is { } job && CrewJobs.Contains(job));
        var seen = new HashSet<(string?, int)>();
        return cast.Concat(crew)
            .Where(c =>
                !c.Adult
                && c.MediaType is "movie" or "tv"
                && !(c.GenreIds ?? []).Any(NoiseGenres.Contains)
            )
            .OrderByDescending(c => c.Popularity)
            .Where(c => seen.Add((c.MediaType, c.Id)))
            .ToList();
    }

    /// <summary>
    /// Whether a cast credit is the person appearing as themselves: "Self", "Himself",
    /// "Herself", their own name, or archive footage of them.
    /// </summary>
    internal static bool IsAppearance(string? character, string? personName)
    {
        var role = character?.Trim();
        if (string.IsNullOrEmpty(role))
            return false;

        if (
            role.Contains("archive footage", StringComparison.OrdinalIgnoreCase)
            || role.Contains("(archive", StringComparison.OrdinalIgnoreCase)
        )
            return true;

        if (
            !string.IsNullOrWhiteSpace(personName)
            && (
                role.Equals(personName, StringComparison.OrdinalIgnoreCase)
                || role.StartsWith(personName + " (", StringComparison.OrdinalIgnoreCase)
            )
        )
            return true;

        var first = new string(role.TakeWhile(char.IsLetter).ToArray());
        return first.ToLowerInvariant() is "self" or "himself" or "herself" or "themselves" or "themself";
    }

    /// <summary>The meta a credit stands for, keyed by its TMDB id as the addon resolves it.</summary>
    internal static StremioMeta? ToMeta(TmdbPersonCredit credit)
    {
        var type = credit.MediaType switch
        {
            "movie" => StremioMediaType.Movie,
            "tv" => StremioMediaType.Series,
            _ => StremioMediaType.Unknown,
        };
        var name = type == StremioMediaType.Movie ? credit.Title : credit.Name;
        if (type == StremioMediaType.Unknown || string.IsNullOrWhiteSpace(name))
            return null;

        var date = type == StremioMediaType.Movie ? credit.ReleaseDate : credit.FirstAirDate;
        DateTime? released = DateTime.TryParseExact(
            date,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var d
        )
            ? d
            : null;

        return new StremioMeta
        {
            Id = $"tmdb:{credit.Id.ToString(CultureInfo.InvariantCulture)}",
            Type = type,
            Name = name,
            Description = credit.Overview,
            Poster = credit.PosterPath is { Length: > 0 } poster
                ? $"https://image.tmdb.org/t/p/w500{poster}"
                : null,
            Background = credit.BackdropPath is { Length: > 0 } backdrop
                ? $"https://image.tmdb.org/t/p/original{backdrop}"
                : null,
            Released = released,
            Year = released?.Year,
            ImdbRating = credit.VoteCount > 0 ? credit.VoteAverage : null,
        };
    }

    /// <summary>
    /// The library's answer and the stand-ins as one list. With a sort the request names, merged
    /// by it, the library's own order kept; without one, the stand-ins follow by popularity.
    /// </summary>
    private List<object> Merge(
        IReadOnlyList<BaseItemDto> local,
        List<RemoteTitle> remote,
        PersonPage page
    )
    {
        if (page.SortBy.Length == 0)
            return [.. local, .. remote];

        var compare = Comparer(page);
        var sorted = remote
            .Select((r, i) => (r, i))
            .OrderBy(x => x.r.SortItem, Comparer<BaseItem>.Create(compare))
            .ThenBy(x => x.i)
            .Select(x => x.r)
            .ToList();
        var localItems = local
            .Select(dto => (Dto: dto, Item: libraryManager.GetItemById(dto.Id)))
            .ToList();

        var merged = new List<object>(local.Count + sorted.Count);
        int i = 0,
            j = 0;
        while (i < localItems.Count || j < sorted.Count)
        {
            var takeRemote =
                i >= localItems.Count
                || (
                    j < sorted.Count
                    && localItems[i].Item is { } item
                    && compare(sorted[j].SortItem, item) < 0
                );
            merged.Add(takeRemote ? sorted[j++] : localItems[i++].Dto);
        }

        return merged;
    }

    private static Comparison<BaseItem> Comparer(PersonPage page) =>
        (a, b) =>
        {
            for (var k = 0; k < page.SortBy.Length; k++)
            {
                var descending =
                    k < page.SortOrder.Length
                        ? page.SortOrder[k] == SortOrder.Descending
                        : page.SortOrder.Length > 0 && page.SortOrder[0] == SortOrder.Descending;
                var c = page.SortBy[k] switch
                {
                    ItemSortBy.SortName => string.Compare(
                        a.SortName,
                        b.SortName,
                        StringComparison.OrdinalIgnoreCase
                    ),
                    ItemSortBy.Name => string.Compare(
                        a.Name,
                        b.Name,
                        StringComparison.OrdinalIgnoreCase
                    ),
                    ItemSortBy.PremiereDate => Nullable.Compare(a.PremiereDate, b.PremiereDate),
                    ItemSortBy.ProductionYear => Nullable.Compare(
                        a.ProductionYear,
                        b.ProductionYear
                    ),
                    ItemSortBy.CommunityRating => Nullable.Compare(
                        a.CommunityRating,
                        b.CommunityRating
                    ),
                    _ => 0,
                };
                if (c != 0)
                    return descending ? -c : c;
            }

            return 0;
        };

    /// <summary>
    /// The page's DTOs: the library's as Jellyfin built them, a stand-in as search builds its
    /// results, kept under its id so that opening it puts the title into the library.
    /// </summary>
    private async Task<List<BaseItemDto>> BuildPageAsync(
        List<object> slice,
        ItemFields[] fields,
        GelatoStremioProvider stremio,
        Guid userId,
        User? user,
        CancellationToken ct
    )
    {
        var remote = slice.OfType<RemoteTitle>().Where(r => r.Library is null).ToList();
        if (remote.Count == 0 && slice.All(e => e is BaseItemDto))
            return slice.Cast<BaseItemDto>().ToList();

        // A meta addon that takes no tmdb: ids (Cinemeta) cannot open a title by its TMDB id:
        // such a stand-in carries its imdb id, as search's results do.
        var withImdb = await WithImdbIdsAsync(remote, stremio, ct).ConfigureAwait(false);

        var options = new DtoOptions(false)
        {
            Fields = SearchActionFilter.SearchResultFields,
            EnableImages = true,
            EnableUserData = false,
        };
        var unknownOptions = new DtoOptions(false)
        {
            Fields = SearchActionFilter.UnknownItemFields,
            EnableImages = true,
            EnableUserData = false,
        };
        var unknownIds = await search
            .FindUnknownIdsAsync(withImdb.Values.Select(r => r.Item.Id), ct)
            .ConfigureAwait(false);

        var dtos = new List<BaseItemDto>(slice.Count);
        foreach (var entry in slice)
        {
            if (entry is BaseItemDto own)
            {
                dtos.Add(own);
                continue;
            }

            if (entry is RemoteTitle { Library: { } held })
            {
                // As Jellyfin's own answer builds it: the request's fields, with user data.
                dtos.Add(
                    dtoService.GetBaseItemDto(
                        held,
                        new DtoOptions(false)
                        {
                            Fields = fields,
                            EnableImages = true,
                            EnableUserData = true,
                        },
                        user
                    )
                );
                continue;
            }

            var title = withImdb[(RemoteTitle)entry];
            var searchId = StremioUri.FromBaseItem(title.Item)!.ToGuid();
            var dto = unknownIds.Contains(title.Item.Id)
                ? search.GetUnknownItemDto(title.Item, unknownOptions)
                : dtoService.GetBaseItemDto(title.Item, options);
            dto.Id = searchId;
            dtos.Add(dto);

            manager.SaveStremioMeta(searchId, title.Meta);
            manager.RememberSearchFolder(userId, searchId, null);
            manager.RememberSearchCopy(userId, searchId, false);
        }

        return dtos;
    }

    private async Task<Dictionary<RemoteTitle, RemoteTitle>> WithImdbIdsAsync(
        List<RemoteTitle> remote,
        GelatoStremioProvider stremio,
        CancellationToken ct
    )
    {
        var result = remote.ToDictionary(r => r, r => r);
        var servesTmdb = new Dictionary<StremioMediaType, bool>();
        foreach (var type in remote.Select(r => r.Meta.Type).Distinct())
            servesTmdb[type] = await stremio.ServesTmdbMetaAsync(type).ConfigureAwait(false);

        var lookups = remote.Where(r => !servesTmdb[r.Meta.Type]).ToList();
        if (lookups.Count == 0)
            return result;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(LookupBudget);
        try
        {
            await Parallel
                .ForEachAsync(
                    lookups,
                    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cts.Token },
                    async (r, t) =>
                    {
                        var tmdbId = r.Meta.Id["tmdb:".Length..];
                        if (
                            await stremio
                                .GetCachedImdbIdFromTmdbAsync(tmdbId, r.Meta.Type, t)
                                .ConfigureAwait(false)
                                is not { } imdbId
                        )
                            return;

                        r.Meta.ImdbId = imdbId;
                        if (manager.IntoBaseItem(r.Meta) is { } item)
                        {
                            lock (result)
                                result[r] = r with { Item = item };
                        }
                    }
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogDebug("Person page: imdb id lookups ran out of time");
        }

        return result;
    }
}
