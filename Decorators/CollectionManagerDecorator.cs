using System.Globalization;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators;

public sealed class CollectionManagerDecorator : ICollectionManager {
    private readonly ICollectionManager _inner;
    private readonly Lazy<GelatoManager> _manager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<CollectionManagerDecorator> _log;

    public CollectionManagerDecorator(
        ICollectionManager inner,
        Lazy<GelatoManager> manager,
        ILibraryManager libraryManager,
        ILogger<CollectionManagerDecorator> log
    ) {
        _inner = inner;
        _manager = manager;
        _libraryManager = libraryManager;
        _log = log;
    }

    public event EventHandler<CollectionCreatedEventArgs>? CollectionCreated {
        add => _inner.CollectionCreated += value;
        remove => _inner.CollectionCreated -= value;
    }

    public event EventHandler<CollectionModifiedEventArgs>? ItemsAddedToCollection {
        add => _inner.ItemsAddedToCollection += value;
        remove => _inner.ItemsAddedToCollection -= value;
    }

    public event EventHandler<CollectionModifiedEventArgs>? ItemsRemovedFromCollection {
        add => _inner.ItemsRemovedFromCollection += value;
        remove => _inner.ItemsRemovedFromCollection -= value;
    }

    public Task<BoxSet> CreateCollectionAsync(CollectionCreationOptions options) =>
        _inner.CreateCollectionAsync(options);

    public async Task AddToCollectionAsync(Guid collectionId, IEnumerable<Guid> itemIds) {
        await _inner.AddToCollectionAsync(collectionId, itemIds).ConfigureAwait(false);

        var collection = _libraryManager.GetItemById(collectionId) as BoxSet;
        if (collection is null)
            return;

        var addedItems = itemIds
            .Select(id => _libraryManager.GetItemById(id))
            .Where(item => item is not null)
            .ToList();

        var gelatoItems = addedItems.Where(_manager.Value.IsGelato).ToList();
        if (gelatoItems.Count == 0)
            return;

        var needsFix = false;

        for (int i = 0; i < collection.LinkedChildren.Length; i++) {
            var linkedChild = collection.LinkedChildren[i];

            if (
                string.IsNullOrEmpty(linkedChild.LibraryItemId)
                && !string.IsNullOrEmpty(linkedChild.Path)
            ) {
                var matchingItem = gelatoItems.FirstOrDefault(item =>
                    item.Path == linkedChild.Path
                );

                if (matchingItem != null) {
                    _log.LogDebug(
                        "Fixing Gelato LinkedChild with path {Path} for item {Id}",
                        linkedChild.Path,
                        matchingItem.Id
                    );

                    collection.LinkedChildren[i] = new LinkedChild {
                        LibraryItemId = matchingItem.Id.ToString("N", CultureInfo.InvariantCulture),
                        Type = LinkedChildType.Manual,
                    };
                    needsFix = true;
                }
            }
        }

        if (needsFix) {
            _log.LogDebug(
                "Fixing {Count} Gelato items in collection {Name}",
                gelatoItems.Count,
                collection.Name
            );
            await collection
                .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    public Task RemoveFromCollectionAsync(Guid collectionId, IEnumerable<Guid> itemIds) =>
        _inner.RemoveFromCollectionAsync(collectionId, itemIds);

    public IEnumerable<BaseItem> CollapseItemsWithinBoxSets(IEnumerable<BaseItem> items, User user) =>
        _inner.CollapseItemsWithinBoxSets(items, user);

    public Task<Folder?> GetCollectionsFolder(bool createIfNeeded) =>
        _inner.GetCollectionsFolder(createIfNeeded);
}
