#nullable disable
#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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

public sealed class GelatoItemRepository : IItemRepository {
    private readonly IItemRepository _inner;
    private readonly IHttpContextAccessor _http;

    public GelatoItemRepository(IItemRepository inner, IHttpContextAccessor http) {
        _inner = inner;
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    // Gelato's own code (PurgeStreams, SaveItems stale cleanup) sets this
    // to true so the DeleteItem guard lets the call through.
    [ThreadStatic]
    public static bool SuppressGuard;

    public void DeleteItem(params IReadOnlyList<Guid> ids) {
        // When Gelato itself is the caller, skip protection.
        if (SuppressGuard) {
            _inner.DeleteItem(ids);
            return;
        }

        // Protect gelato-stream items from being deleted by the library scanner.
        var protected_ids = new HashSet<Guid>();
        foreach (var id in ids) {
            var item = _inner.RetrieveItem(id);
            if (item is not null && GelatoManager.HasStreamTag(item)) {
                protected_ids.Add(id);
            }
        }
        if (protected_ids.Count > 0) {
            var filtered = ids.Where(id => !protected_ids.Contains(id)).ToList();
            if (filtered.Count > 0)
                _inner.DeleteItem(filtered);
            return;
        }
        _inner.DeleteItem(ids);
    }

    public void SaveItems(IReadOnlyList<BaseItem> items, CancellationToken cancellationToken) {
        // Prevent the library scanner from overwriting Gelato stream items.
        // When the scanner discovers .strm files on disk, it resolves them into
        // new BaseItem objects WITHOUT the gelato-stream tag. If we let these
        // through, they'd overwrite the tagged items and break Gelato's tracking.
        var dominated = new HashSet<Guid>();
        foreach (var item in items) {
            if (GelatoManager.HasStreamTag(item)) continue; // Gelato's own saves pass through
            var existing = _inner.RetrieveItem(item.Id);
            if (existing is not null && GelatoManager.HasStreamTag(existing)) {
                dominated.Add(item.Id);
            }
        }
        if (dominated.Count > 0) {
            var filtered = items.Where(i => !dominated.Contains(i.Id)).ToList();
            _inner.SaveItems(filtered, cancellationToken);
            return;
        }
        _inner.SaveItems(items, cancellationToken);
    }

    public void SaveImages(BaseItem item) => _inner.SaveImages(item);

    public BaseItem RetrieveItem(Guid id) => _inner.RetrieveItem(id);

    public QueryResult<BaseItem> GetItems(InternalItemsQuery filter) {
        return _inner.GetItems(ApplyFilters(filter));
    }

    public IReadOnlyList<Guid> GetItemIdsList(InternalItemsQuery filter) =>
        _inner.GetItemIdsList(ApplyFilters(filter));

    public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery filter) {
        return _inner.GetItemList(ApplyFilters(filter));
    }

    public IReadOnlyList<BaseItem> GetNoScopeItemList(InternalItemsQuery filter) {
        return _inner.GetItemList(filter);
    }

    public InternalItemsQuery ApplyFilters(InternalItemsQuery filter) {
        var ctx = _http?.HttpContext;
        var filterUnreleased = GelatoPlugin.Instance.Configuration.FilterUnreleased;
        var bufferDays = GelatoPlugin.Instance.Configuration.FilterUnreleasedBufferDays;

        if (ctx is not null && ctx.IsApiListing() && filter.IsDeadPerson is null) {
            filter.IsDeadPerson = null;
            if (
                (
                    !filter.IncludeItemTypes.Any()
                    || filter
                        .IncludeItemTypes.Intersect(
                            new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode }
                        )
                        .Any()
                )
            ) {
                if (filter.ExcludeTags is null || filter.ExcludeTags.Length == 0) {
                    filter.ExcludeTags = new[] { GelatoManager.StreamTag };
                }
                if (filter.MaxPremiereDate is null && filterUnreleased) {
                    // we dont have access to the query so can make a proper statement.
                    var days =
                        (
                            filter.IncludeItemTypes.Contains(BaseItemKind.Series)
                            || filter.IncludeItemTypes.Contains(BaseItemKind.Episode)
                        )
                            ? 0
                            : bufferDays;
                    filter.MaxPremiereDate = DateTime.Today.AddDays((double)days);
                }
            }
        }
        else if (!filter.IncludeItemTypes.Contains(BaseItemKind.Person)) {
            filter.IsDeadPerson = null;
        }
        else if (filter.IsMissing == true) {
            // jf deletes virtual items when theres a valid primary version. So just dont return it
            filter.ExcludeTags = new[] { GelatoManager.StreamTag };
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

    public Task ReattachUserDataAsync(BaseItem item, CancellationToken cancellationToken) =>
        _inner.ReattachUserDataAsync(item, cancellationToken);
}
