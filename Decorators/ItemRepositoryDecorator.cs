using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;

namespace Gelato.Decorators
{
    public sealed class ItemRepositoryDecorator : IItemRepository
    {
        private readonly IItemRepository _inner;
        private readonly IHttpContextAccessor _http;

        public ItemRepositoryDecorator(IItemRepository inner, IHttpContextAccessor http)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        public void DeleteItem(Guid id) => _inner.DeleteItem(id);

        public void SaveItems(IReadOnlyList<BaseItem> items, CancellationToken cancellationToken)
            => _inner.SaveItems(items, cancellationToken);

        public void SaveImages(BaseItem item) => _inner.SaveImages(item);

        public bool IsItemsActionName(string name)
        {
            //Console.Write($"{name}\n");
            return string.Equals(name, "GetItems", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "GetItem", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "GetItemLegacy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "GetNextUp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "GetLatestMedia", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "GetLatestMediaLegacy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "GetResumeItemsLegacy", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "GetRecommendedPrograms", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "GetEpisodes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "GetItemsByUserIdLegacy", StringComparison.OrdinalIgnoreCase);
        }

        public BaseItem RetrieveItem(Guid id) => _inner.RetrieveItem(id);
        private const string AltTag = "Alternate";

        private bool ShouldExcludeAlternate()
        {
            var ctx = _http?.HttpContext;
            if (ctx == null) return false;

            return ctx.Items.TryGetValue("actionName", out var actionObj)
                && actionObj is string actionName
                && !string.IsNullOrEmpty(actionName)
                && IsItemsActionName(actionName);
        }

        private void ApplyAlternateFilter(InternalItemsQuery filter)
        {
            if (!ShouldExcludeAlternate()) return;

            var tags = filter.ExcludeTags ?? Array.Empty<string>();
            if (!tags.Contains(AltTag, StringComparer.OrdinalIgnoreCase))
                filter.ExcludeTags = tags.Append(AltTag).ToArray();
        }

        public QueryResult<BaseItem> GetItems(InternalItemsQuery filter)
        {
            ApplyAlternateFilter(filter);
            return _inner.GetItems(filter);
        }

        public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery filter)
        {
            ApplyAlternateFilter(filter);
            return _inner.GetItemList(filter);
        }

        public IReadOnlyList<Guid> GetItemIdsList(InternalItemsQuery filter)
            => _inner.GetItemIdsList(filter);

        //public bool GetIsPlayed(User user, Guid id, bool recursive)
        //    => _inner.GetIsPlayed(user, id, recursive);

        public IReadOnlyList<BaseItem> GetLatestItemList(InternalItemsQuery filter, CollectionType collectionType)
            => _inner.GetLatestItemList(filter, collectionType);

        public IReadOnlyList<string> GetNextUpSeriesKeys(InternalItemsQuery filter, DateTime dateCutoff)
            => _inner.GetNextUpSeriesKeys(filter, dateCutoff);

        public void UpdateInheritedValues() => _inner.UpdateInheritedValues();

        public int GetCount(InternalItemsQuery filter) => _inner.GetCount(filter);

        public ItemCounts GetItemCounts(InternalItemsQuery filter)
            => _inner.GetItemCounts(filter);

        public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetGenres(InternalItemsQuery filter)
            => _inner.GetGenres(filter);

        public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetMusicGenres(InternalItemsQuery filter)
            => _inner.GetMusicGenres(filter);

        public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetStudios(InternalItemsQuery filter)
            => _inner.GetStudios(filter);

        public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetArtists(InternalItemsQuery filter)
            => _inner.GetArtists(filter);

        public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetAlbumArtists(InternalItemsQuery filter)
            => _inner.GetAlbumArtists(filter);

        public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetAllArtists(InternalItemsQuery filter)
            => _inner.GetAllArtists(filter);

        public IReadOnlyList<string> GetMusicGenreNames()
            => _inner.GetMusicGenreNames();

        public IReadOnlyList<string> GetStudioNames()
            => _inner.GetStudioNames();

        public IReadOnlyList<string> GetGenreNames()
            => _inner.GetGenreNames();

        public IReadOnlyList<string> GetAllArtistNames()
            => _inner.GetAllArtistNames();

        public Task<bool> ItemExistsAsync(Guid id)
            => _inner.ItemExistsAsync(id);
    }
}