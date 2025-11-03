#nullable disable
#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Gelato.Common;
using Gelato.Common;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;

namespace Gelato.Decorators;

public sealed class GelatoItemRepository : IItemRepository
{
    private readonly IItemRepository _inner;
    private readonly IHttpContextAccessor _http;

    public GelatoItemRepository(IItemRepository inner, IHttpContextAccessor http)
    {
        _inner = inner;
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public void DeleteItem(params IReadOnlyList<Guid> ids) => _inner.DeleteItem(ids);

    public void SaveItems(IReadOnlyList<BaseItem> items, CancellationToken cancellationToken) =>
        _inner.SaveItems(items, cancellationToken);

    public void SaveImages(BaseItem item) => _inner.SaveImages(item);

    public BaseItem RetrieveItem(Guid id) => _inner.RetrieveItem(id);

    public QueryResult<BaseItem> GetItems(InternalItemsQuery filter)
    {
        return _inner.GetItems(ApplyFilters(filter));
    }

    public IReadOnlyList<Guid> GetItemIdsList(InternalItemsQuery filter) =>
        _inner.GetItemIdsList(ApplyFilters(filter));

    public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery filter)
    {
        return _inner.GetItemList(ApplyFilters(filter));
    }

    public IReadOnlyList<BaseItem> GetNoScopeItemList(InternalItemsQuery filter)
    {
        return _inner.GetItemList(filter);
    }

    public InternalItemsQuery ApplyFilters(InternalItemsQuery filter)
    {
        var ctx = _http?.HttpContext;
        var filterUnreleased = GelatoPlugin.Instance.Configuration.FilterUnreleased;
        var bufferDays = GelatoPlugin.Instance.Configuration.FilterUnreleasedBufferDays;

        if (ctx is not null && ctx.IsApiListing() && filter.IsDeadPerson is null)
        {
            filter.IsDeadPerson = null;

            if (
                (
                    !filter.IncludeItemTypes.Any()
                    || filter
                        .IncludeItemTypes.Intersect(
                            new[] { BaseItemKind.Movie, BaseItemKind.Series }
                        )
                        .Any()
                )
            )
            {
                if (filter.IsVirtualItem is null)
                {
                    filter.IsVirtualItem = false;
                }
                if (filter.MaxPremiereDate is null && filterUnreleased)
                {
                    filter.MaxPremiereDate = DateTime.Today.AddDays((double)bufferDays);
                }
            }
        }
        return filter;
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
