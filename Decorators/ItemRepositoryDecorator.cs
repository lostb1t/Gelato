#nullable disable
#pragma warning disable CS1591

using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
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

    public void DeleteItem(params IReadOnlyList<Guid> ids) => inner.DeleteItem(ids);

    public void SaveItems(IReadOnlyList<BaseItem> items, CancellationToken cancellationToken) =>
        inner.SaveItems(items, cancellationToken);

    public void SaveImages(BaseItem item) => inner.SaveImages(item);

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
        // Internal Gelato/library lookups should never be reshaped by listing filters.
        // Path-based queries are commonly used to resolve configured root folders.
        if (filter.IsDeadPerson == true || !string.IsNullOrWhiteSpace(filter.Path))
            return filter;

        var ctx = _http.HttpContext;
        var isListingIntent =
            ctx is not null && (ctx.IsApiListing() || ctx.IsHomeScreenSectionListing());
        if (!isListingIntent)
            return filter;

        var filterUnreleased = GelatoPlugin.Instance!.Configuration.FilterUnreleased;
        var bufferDays = GelatoPlugin.Instance.Configuration.FilterUnreleasedBufferDays;
        var includeTypes = filter.IncludeItemTypes;
        var hasIncludeTypes = includeTypes.Length != 0;
        var includesPerson = includeTypes.Contains(BaseItemKind.Person);
        var isStreamTagQuery = filter.Tags.Contains(
            GelatoManager.StreamTag,
            StringComparer.OrdinalIgnoreCase
        );
        var isTargetedLookup =
            filter.ItemIds.Length > 0 || (ctx is not null && ctx.IsSingleItemList());

        // Targeted ItemIds lookups are generally internal existence/permission checks.
        // Keep those untouched so the caller gets strict results from the underlying query.
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

        // Do not override queries that explicitly target stream-tagged rows.
        if (!isStreamTagQuery && filter.ExcludeTags.Length == 0)
            filter.ExcludeTags = [GelatoManager.StreamTag];

        if (filter.MaxPremiereDate is not null || !filterUnreleased)
            return filter;

        var isPremiereFilteredQuery =
            !hasIncludeTypes || includeTypes.Intersect(PremiereFilterMediaKinds).Any();
        if (!isPremiereFilteredQuery)
            return filter;

        // Series/episodes should include currently airing content. Movies can use the buffer.
        var days =
            includeTypes.Contains(BaseItemKind.Series)
            || includeTypes.Contains(BaseItemKind.Season)
            || includeTypes.Contains(BaseItemKind.Episode)
                ? 0
                : bufferDays;
        filter.MaxPremiereDate = DateTime.Today.AddDays(days);

        return filter;
    }

    public IReadOnlyList<BaseItem> GetLatestItemList(
        InternalItemsQuery filter,
        CollectionType collectionType
    ) => inner.GetLatestItemList(filter, collectionType);

    public IReadOnlyList<string> GetNextUpSeriesKeys(
        InternalItemsQuery filter,
        DateTime dateCutoff
    ) => inner.GetNextUpSeriesKeys(filter, dateCutoff);

    public void UpdateInheritedValues() => inner.UpdateInheritedValues();

    public int GetCount(InternalItemsQuery filter) => inner.GetCount(filter);

    public ItemCounts GetItemCounts(InternalItemsQuery filter) => inner.GetItemCounts(filter);

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

    public Task<bool> ItemExistsAsync(Guid id) => inner.ItemExistsAsync(id);

    public bool GetIsPlayed(User user, Guid id, bool recursive) =>
        inner.GetIsPlayed(user, id, recursive);

    public IReadOnlyDictionary<string, MusicArtist[]> FindArtists(
        IReadOnlyList<string> artistNames
    ) => inner.FindArtists(artistNames);

    public Task ReattachUserDataAsync(BaseItem item, CancellationToken cancellationToken) =>
        inner.ReattachUserDataAsync(item, cancellationToken);
}
