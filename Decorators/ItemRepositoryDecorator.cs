#nullable disable
#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;

namespace Gelato.Decorators;

public sealed class ItemRepositoryDecorator : IItemRepository
{
    private readonly IItemRepository _inner;

    public ItemRepositoryDecorator(IItemRepository inner)
    {
        _inner = inner;
    }

    public void DeleteItem(params IReadOnlyList<Guid> ids) => _inner.DeleteItem(ids);

    public void SaveItems(IReadOnlyList<BaseItem> items, CancellationToken cancellationToken) =>
        _inner.SaveItems(items, cancellationToken);

    public void SaveImages(BaseItem item) => _inner.SaveImages(item);

    public BaseItem RetrieveItem(Guid id) => _inner.RetrieveItem(id);

    public QueryResult<BaseItem> GetItems(InternalItemsQuery filter)
    {
        filter.IsVirtualItem = filter.IsVirtualItem == true ? null : false;
        return _inner.GetItems(filter);
    }

    public IReadOnlyList<Guid> GetItemIdsList(InternalItemsQuery filter) =>
        _inner.GetItemIdsList(filter);

    public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery filter)
    {
        filter.IsVirtualItem = filter.IsVirtualItem == true ? null : false;
        return _inner.GetItemList(filter);
    }

    public IReadOnlyList<BaseItem> GetLatestItemList(
        InternalItemsQuery filter,
        CollectionType collectionType
    ) => _inner.GetLatestItemList(filter, collectionType);

    public IReadOnlyList<string> GetNextUpSeriesKeys(
        InternalItemsQuery filter,
        DateTime dateCutoff
    ) => _inner.GetNextUpSeriesKeys(filter, dateCutoff);

    public void UpdateInheritedValues() => _inner.UpdateInheritedValues();

    public int GetCount(InternalItemsQuery filter) => _inner.GetCount(filter);

    public ItemCounts GetItemCounts(InternalItemsQuery filter) => _inner.GetItemCounts(filter);

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetGenres(
        InternalItemsQuery filter
    ) => _inner.GetGenres(filter);

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetMusicGenres(
        InternalItemsQuery filter
    ) => _inner.GetMusicGenres(filter);

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetStudios(
        InternalItemsQuery filter
    ) => _inner.GetStudios(filter);

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetArtists(
        InternalItemsQuery filter
    ) => _inner.GetArtists(filter);

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetAlbumArtists(
        InternalItemsQuery filter
    ) => _inner.GetAlbumArtists(filter);

    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetAllArtists(
        InternalItemsQuery filter
    ) => _inner.GetAllArtists(filter);

    public IReadOnlyList<string> GetMusicGenreNames() => _inner.GetMusicGenreNames();

    public IReadOnlyList<string> GetStudioNames() => _inner.GetStudioNames();

    public IReadOnlyList<string> GetGenreNames() => _inner.GetGenreNames();

    public IReadOnlyList<string> GetAllArtistNames() => _inner.GetAllArtistNames();

    public Task<bool> ItemExistsAsync(Guid id) => _inner.ItemExistsAsync(id);

    public bool GetIsPlayed(User user, Guid id, bool recursive) =>
        _inner.GetIsPlayed(user, id, recursive);

    public IReadOnlyDictionary<string, MusicArtist[]> FindArtists(
        IReadOnlyList<string> artistNames
    ) => _inner.FindArtists(artistNames);
}
