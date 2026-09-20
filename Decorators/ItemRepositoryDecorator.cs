#nullable disable
#pragma warning disable CS1591

using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;

namespace Gelato.Decorators;

public sealed class GelatoItemRepository(IItemRepository inner, IHttpContextAccessor http)
    : IItemRepository
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

        var ctx = _http.HttpContext;
        var isListingIntent =
            ctx is not null && (ctx.IsApiListing() || ctx.IsHomeScreenSectionListing());
        if (!isListingIntent)
            return filter;

        var filterUnreleased = GelatoPlugin.Instance!.Configuration.FilterUnreleased;
        var bufferDays = GelatoPlugin.Instance.Configuration.FilterUnreleasedBufferDays;
        var hasIncludeTypes = includeTypes.Length != 0;
        var isStreamTagQuery = filter.Tags.Contains(
            GelatoManager.StreamTag,
            StringComparer.OrdinalIgnoreCase
        );
        var isTargetedLookup =
            ctx is not null
            && ((filter.ItemIds.Length > 0 && ctx.HasExplicitItemIds()) || ctx.IsSingleItemList());

        // Targeted ItemIds lookups are generally internal existence/permission checks.
        // Keep those untouched so the caller gets strict results from the underlying query.
        // The ids must have come from the caller: Jellyfin 12 turns a searchTerm into a list of
        // ItemIds before querying, so treating any populated ItemIds as targeted would let every
        // search return the hidden stream rows alongside the item they belong to.
        if (isTargetedLookup)
            return filter;

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

        if (filter.MaxPremiereDate is not null || !filterUnreleased)
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
    /// The Gelato items the unreleased filter hides, for the configured buffer. Jellyfin answers a
    /// non-recursive listing from a folder's cached children instead of querying the repository,
    /// so <see cref="Filters.UnreleasedListingFilter"/> needs the same set to finish the job on the
    /// response.
    /// </summary>
    public Guid[] GetUnreleasedIds(int bufferDays) =>
        GetUnreleasedGelatoIds(DateTime.Today.AddDays(-bufferDays));

    private Guid[] GetUnreleasedGelatoIds(DateTime cutoff) =>
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
    ) => inner.GetLatestItemList(filter, collectionType);

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

    public QueryFiltersLegacy GetQueryFiltersLegacy(InternalItemsQuery filter) =>
        inner.GetQueryFiltersLegacy(filter);

    public IReadOnlyList<string> GetTagNames(InternalItemsQuery filter) =>
        inner.GetTagNames(filter);

    public Task<bool> ItemExistsAsync(Guid id) => inner.ItemExistsAsync(id);

    public bool GetIsPlayed(User user, Guid id, bool recursive) =>
        inner.GetIsPlayed(user, id, recursive);
}
