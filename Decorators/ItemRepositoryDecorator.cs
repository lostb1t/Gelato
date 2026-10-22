#nullable disable
#pragma warning disable CS1591

using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Gelato.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;

namespace Gelato.Decorators;

public sealed class GelatoItemRepository(
    IItemRepository inner,
    IHttpContextAccessor http,
    ItemWriteCounter writes,
    ItemIdLookup lookup
) : IItemRepository
{
    private static readonly BaseItemKind[] ListScopeMediaKinds =
    [
        BaseItemKind.Movie,
        BaseItemKind.Series,
        BaseItemKind.Episode,
    ];

    private static readonly BaseItemKind[] PremiereFilterMediaKinds =
    [
        BaseItemKind.Movie,
        BaseItemKind.Series,
        BaseItemKind.Season,
        BaseItemKind.Episode,
    ];

    private readonly IHttpContextAccessor _http =
        http ?? throw new ArgumentNullException(nameof(http));

    public BaseItem RetrieveItem(Guid id) => inner.RetrieveItem(id);

    public QueryResult<BaseItem> GetItems(InternalItemsQuery filter)
    {
        return inner.GetItems(ApplyFilters(filter));
    }

    public IReadOnlyList<Guid> GetItemIdsList(InternalItemsQuery filter) =>
        inner.GetItemIdsList(ApplyFilters(filter));

    public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery filter)
    {
        return inner.GetItemList(ApplyFilters(filter));
    }

    private InternalItemsQuery ApplyFilters(InternalItemsQuery filter)
    {
        var includeTypes = filter.IncludeItemTypes;
        var includesPerson = includeTypes.Contains(BaseItemKind.Person);
        // Internal Gelato/library lookups should never be reshaped by listing filters.
        // Path-based queries are commonly used to resolve configured root folders.
        if (filter.IsDeadPerson == true || !string.IsNullOrWhiteSpace(filter.Path))
        {
            if (!includesPerson)
            {
                filter.IsDeadPerson = null;
            }
            return filter;
        }

        // Jellyfin loads a folder's children with this exact query and keeps the result in memory
        // for the rest of the server's life (Folder.GetCachedChildren, and the same shape again for
        // a library scan's GetChildrenForValidation). Rewriting it puts the listing filter's result
        // into that cache: a non-recursive listing then stayed short even after the filter was
        // turned off, and a scan would compare the library against a set of children it trimmed.
        if (
            filter.ParentId != Guid.Empty
            && filter.User is null
            && !filter.GroupByPresentationUniqueKey
            && includeTypes.Length == 0
        )
            return filter;

        // Targeted ItemIds lookups are generally internal existence/permission checks.
        // Keep those untouched so the caller gets strict results from the underlying query.
        // The ids must have come from the caller: Jellyfin 12 turns a searchTerm into a list of
        // ItemIds before querying, so treating any populated ItemIds as targeted would let every
        // search return the hidden stream rows alongside the item they belong to.
        var isListingIntent = _http.ReadRequest(
            ctx =>
                (ctx.IsApiListing() || ctx.IsHomeScreenSectionListing())
                && !(
                    (filter.ItemIds.Length > 0 && ctx.HasExplicitItemIds())
                    || ctx.IsSingleItemList()
                ),
            false
        );
        if (!isListingIntent)
            return filter;

        var filterUnreleased = GelatoPlugin.Instance!.Configuration.FilterUnreleased;
        var bufferDays = GelatoPlugin.Instance.Configuration.FilterUnreleasedBufferDays;
        var hasIncludeTypes = includeTypes.Length != 0;
        var isStreamTagQuery = filter.Tags.Contains(
            GelatoManager.StreamTag,
            StringComparer.OrdinalIgnoreCase
        );

        if (!includesPerson)
            filter.IsDeadPerson = null;

        // Query-shape based media list detection: empty IncludeItemTypes is broad-list scope,
        // otherwise only media kinds we manage are considered for stream-row exclusion.
        var isMediaListQuery =
            !hasIncludeTypes || includeTypes.Intersect(ListScopeMediaKinds).Any();
        if (!isMediaListQuery)
            return filter;

        // Do not override queries that explicitly target stream-tagged rows. Resume queries list
        // the version that was played, and Jellyfin hides linked versions from all other lists.
        if (!isStreamTagQuery && filter.ExcludeTags.Length == 0 && filter.IsResumable != true)
            filter.ExcludeTags = [GelatoManager.StreamTag];

        ExcludePending(filter);

        // The Upcoming list asks for unaired episodes on purpose. HiddenListingFilter still
        // drops the episodes of a series the filter hides.
        if (
            filter.MaxPremiereDate is not null
            || !filterUnreleased
            || _http.ReadRequest(ctx => ctx.IsUpcomingEpisodes(), false)
        )
            return filter;

        var isPremiereFilteredQuery =
            !hasIncludeTypes || includeTypes.Intersect(PremiereFilterMediaKinds).Any();
        if (!isPremiereFilteredQuery)
            return filter;

        // Gelato items carry their release in EndDate (digital for movies, premiere for
        // series/episodes; sentinel 9999 = no release date known). Setting MaxEndDate on the query
        // would also drop every row whose EndDate is null, which is every native item, collection,
        // playlist and library folder, so the unreleased Gelato items are excluded by id instead.
        // The buffer keeps an item hidden for that many days after its release, as the setting
        // says ("Items released within these many days will be hidden"), which is also how
        // StremioMeta.IsReleased reads it for the addon search. Adding the days to the cutoff
        // instead of subtracting them listed items that many days *before* their release: a buffer
        // of 120 put a film premiering in 89 days into the library.
        var unreleased = GetUnreleasedGelatoIds(DateTime.Today.AddDays(-bufferDays));
        if (unreleased.Length > 0)
            filter.ExcludeItemIds = [.. filter.ExcludeItemIds, .. unreleased];

        return filter;
    }

    /// <summary>
    /// Leaves the titles opened from search and never played out of a listing, unless the listing
    /// is a search: a search still finds them.
    /// </summary>
    private void ExcludePending(InternalItemsQuery filter)
    {
        if (
            !GelatoPlugin.Instance!.Configuration.HideUnplayedSearchTitles
            || _http.ReadRequest(ctx => ctx.IsSearchRequest(), true)
        )
            return;

        var kept = GetPending();
        if (
            kept.Ids.Length > 0
            && !_http.ReadRequest(ctx => IsInsidePending(ctx, kept.Set), true)
        )
            filter.ExcludeItemIds = [.. filter.ExcludeItemIds, .. kept.Ids];
    }

    /// <summary>
    /// The library counts (<c>/Items/Counts</c>) leave the hidden titles out like the listings.
    /// </summary>
    public void ExcludePendingFromCounts(InternalItemsQuery filter)
    {
        if (_http.ReadRequest(ctx => ctx.GetActionName() == "GetItemCounts", false))
            ExcludePending(filter);
    }

    /// <summary>
    /// Whether the request lists what is inside a hidden title (its seasons, its episodes): the
    /// page of a title just opened from search shows them.
    /// </summary>
    public static bool IsInsidePending(HttpContext ctx, IReadOnlySet<Guid> hidden)
    {
        foreach (var key in (string[])["seriesId", "parentId", "seasonId"])
        {
            var value = ctx.Request.RouteValues[key]?.ToString() ?? ctx.Request.Query[key].ToString();
            if (Guid.TryParse(value, out var id) && hidden.Contains(id))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The items <see cref="PendingSearchTitles"/> keeps out of listings: the titles, and a
    /// series' seasons and episodes with it. Empty while the setting is off.
    /// </summary>
    public IReadOnlySet<Guid> GetPendingIds() =>
        GelatoPlugin.Instance?.Configuration.HideUnplayedSearchTitles == true
            ? GetPending().Set
            : new HashSet<Guid>();

    private sealed record PendingIds(long Version, Guid[] Ids, HashSet<Guid> Set);

    private volatile PendingIds _pending;

    // Kept like the unreleased set, until the next item write: the mark is one, and a series'
    // episodes arrive with its refresh.
    private PendingIds GetPending()
    {
        var version = writes.Version;
        var kept = _pending;
        if (kept is not null && kept.Version == version)
            return kept;

        var titles = lookup.WithTag(PendingSearchTitles.Tag);
        Guid[] ids = titles.Length == 0
            ? []
            :
            [
                .. titles,
                .. inner.GetItemIdsList(new InternalItemsQuery { AncestorIds = titles }),
            ];
        var read = new PendingIds(version, ids, [.. ids]);
        if (writes.IsCounting)
            _pending = read;
        return read;
    }

    /// <summary>
    /// The Gelato items the unreleased filter hides, for the configured buffer. Jellyfin answers a
    /// non-recursive listing from a folder's cached children instead of querying the repository,
    /// so <see cref="Filters.HiddenListingFilter"/> needs the same set to finish the job on the
    /// response.
    /// </summary>
    public IReadOnlySet<Guid> GetUnreleasedIds(int bufferDays) =>
        GetUnreleased(DateTime.Today.AddDays(-bufferDays)).Set;

    /// <summary>
    /// Makes the next listing read the unreleased set again, whatever was written since.
    /// </summary>
    public void ForgetUnreleasedIds() => writes.Bump();

    private Guid[] GetUnreleasedGelatoIds(DateTime cutoff) => GetUnreleased(cutoff).Ids;

    private sealed record UnreleasedIds(long Cutoff, long Version, Guid[] Ids, HashSet<Guid> Set);

    private volatile UnreleasedIds _unreleased;

    // Every listing asks for the set, a home screen about twenty times, and reading it walks
    // every movie, series, season and episode (15 to 17 ms on a library of 4.7k), for a set that
    // changes a few times a day. It is kept until the next item write (ItemWriteCounter), the
    // next day or another buffer (both move the cutoff), and a saved configuration or a purge
    // (GelatoManager.ClearCache). The count is taken before the query: a write that lands while
    // it runs leaves the answer stored under a count that is no longer current, so the next
    // listing reads again. Neither the array nor the set is ever changed after it is stored.
    // Nothing is kept while nobody counts the writes (the persistence service is not decorated,
    // which should not happen): a set that is never thrown away would hide and show the wrong
    // items for good.
    private UnreleasedIds GetUnreleased(DateTime cutoff)
    {
        var version = writes.Version;
        var kept = _unreleased;
        if (kept is not null && kept.Cutoff == cutoff.Ticks && kept.Version == version)
            return kept;

        var ids = QueryUnreleasedGelatoIds(cutoff);
        var read = new UnreleasedIds(cutoff.Ticks, version, ids, [.. ids]);
        if (writes.IsCounting)
            _unreleased = read;
        return read;
    }

    private Guid[] QueryUnreleasedGelatoIds(DateTime cutoff) =>
        inner
            .GetItemIdsList(
                new InternalItemsQuery
                {
                    IncludeItemTypes = PremiereFilterMediaKinds,
                    // MinEndDate is inclusive; items released on the cutoff itself stay listed.
                    MinEndDate = cutoff.AddTicks(1),
                    HasAnyProviderId = new Dictionary<string, string>
                    {
                        ["Stremio"] = string.Empty,
                    },
                    ExcludeTags = [GelatoManager.StreamTag],
                }
            )
            .ToArray();

    public IReadOnlyList<BaseItem> GetLatestItemList(
        InternalItemsQuery filter,
        CollectionType collectionType
    )
    {
        ExcludePending(filter);
        return inner.GetLatestItemList(filter, collectionType);
    }

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetGenres(
        InternalItemsQuery filter
    ) => inner.GetGenres(filter);

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetMusicGenres(
        InternalItemsQuery filter
    ) => inner.GetMusicGenres(filter);

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetStudios(
        InternalItemsQuery filter
    ) => inner.GetStudios(filter);

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetArtists(
        InternalItemsQuery filter
    ) => inner.GetArtists(filter);

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetAlbumArtists(
        InternalItemsQuery filter
    ) => inner.GetAlbumArtists(filter);

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetAllArtists(
        InternalItemsQuery filter
    ) => inner.GetAllArtists(filter);

    public IReadOnlyList<string> GetMusicGenreNames() => inner.GetMusicGenreNames();

    public IReadOnlyList<string> GetStudioNames() => inner.GetStudioNames();

    public IReadOnlyList<string> GetGenreNames() => inner.GetGenreNames();

    public IReadOnlyList<string> GetAllArtistNames() => inner.GetAllArtistNames();

    public IReadOnlyList<string> GetMediaStreamLanguages(
        InternalItemsQuery filter,
        MediaStreamType mediaStreamType
    ) => inner.GetMediaStreamLanguages(filter, mediaStreamType);

    // The tag lists of a library's filters (/Items/Filters, /Items/Filters2) leave out Gelato's
    // mark for a title opened from search.
    public QueryFiltersLegacy GetQueryFiltersLegacy(InternalItemsQuery filter)
    {
        var filters = inner.GetQueryFiltersLegacy(filter);
        if (filters.Tags is { } tags)
            filters.Tags = WithoutPendingTag(tags);
        return filters;
    }

    public IReadOnlyList<string> GetTagNames(InternalItemsQuery filter) =>
        WithoutPendingTag(inner.GetTagNames(filter));

    private static string[] WithoutPendingTag(IEnumerable<string> tags) =>
        [
            .. tags.Where(t =>
                !t.Equals(PendingSearchTitles.Tag, StringComparison.OrdinalIgnoreCase)
            ),
        ];

    public Task<bool> ItemExistsAsync(Guid id) => inner.ItemExistsAsync(id);

    public bool GetIsPlayed(User user, Guid id, bool recursive) =>
        inner.GetIsPlayed(user, id, recursive);
}
