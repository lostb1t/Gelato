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
        var ctx = _http.HttpContext;
        var filterUnreleased = GelatoPlugin.Instance!.Configuration.FilterUnreleased;
        var bufferDays = GelatoPlugin.Instance.Configuration.FilterUnreleasedBufferDays;

        if (ctx is not null && !ctx.IsSingleItemList() && filter.IsDeadPerson is null)
        {
            filter.IsDeadPerson = null;
            if (
                filter.IncludeItemTypes.Length != 0
                && !filter
                    .IncludeItemTypes.Intersect(
                        [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode]
                    )
                    .Any()
            )
                return filter;
            if (filter.ExcludeTags.Length == 0)
            {
                filter.ExcludeTags = [GelatoManager.StreamTag];
            }

            if (filter.MaxPremiereDate is not null || !filterUnreleased)
                return filter;

            // we dont have access to the query so can make a proper statement.
            var days =
                filter.IncludeItemTypes.Contains(BaseItemKind.Series)
                || filter.IncludeItemTypes.Contains(BaseItemKind.Episode)
                    ? 0
                    : bufferDays;
            filter.MaxPremiereDate = DateTime.Today.AddDays(days);
        }
        else if (!filter.IncludeItemTypes.Contains(BaseItemKind.Person))
        {
            filter.IsDeadPerson = null;
        }

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
