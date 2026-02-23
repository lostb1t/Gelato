using System.Globalization;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators;

public sealed class CollectionManagerDecorator(
    ICollectionManager inner,
    Lazy<GelatoManager> manager,
    ILibraryManager libraryManager,
    ILogger<CollectionManagerDecorator> log
) : ICollectionManager
{
    public event EventHandler<CollectionCreatedEventArgs>? CollectionCreated
    {
        add => inner.CollectionCreated += value;
        remove => inner.CollectionCreated -= value;
    }

    public event EventHandler<CollectionModifiedEventArgs>? ItemsAddedToCollection
    {
        add => inner.ItemsAddedToCollection += value;
        remove => inner.ItemsAddedToCollection -= value;
    }

    public event EventHandler<CollectionModifiedEventArgs>? ItemsRemovedFromCollection
    {
        add => inner.ItemsRemovedFromCollection += value;
        remove => inner.ItemsRemovedFromCollection -= value;
    }

    public Task<BoxSet> CreateCollectionAsync(CollectionCreationOptions options) =>
        inner.CreateCollectionAsync(options);

    public async Task AddToCollectionAsync(Guid collectionId, IEnumerable<Guid> itemIds)
    {
        var guids = itemIds as Guid[] ?? itemIds.ToArray();
        await inner.AddToCollectionAsync(collectionId, guids).ConfigureAwait(false);

        if (libraryManager.GetItemById(collectionId) is not BoxSet collection)
            return;

        var gelatoItems = guids
            .Select(libraryManager.GetItemById)
            .Where(item => item is not null && item.IsGelato())
            .ToList();

        if (gelatoItems.Count == 0)
            return;

        var needsFix = false;

        for (var i = 0; i < collection.LinkedChildren.Length; i++)
        {
            var linkedChild = collection.LinkedChildren[i];

            if (
                string.IsNullOrEmpty(linkedChild.LibraryItemId)
                && !string.IsNullOrEmpty(linkedChild.Path)
            )
            {
                var matchingItem = gelatoItems.FirstOrDefault(item =>
                    item?.Path == linkedChild.Path
                );

                if (matchingItem != null)
                {
                    log.LogDebug(
                        "Fixing Gelato LinkedChild with path {Path} for item {Id}",
                        linkedChild.Path,
                        matchingItem.Id
                    );

                    collection.LinkedChildren[i] = new LinkedChild
                    {
                        LibraryItemId = matchingItem.Id.ToString("N", CultureInfo.InvariantCulture),
                        Type = LinkedChildType.Manual,
                    };
                    needsFix = true;
                }
            }
        }

        if (needsFix)
        {
            log.LogDebug(
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
        inner.RemoveFromCollectionAsync(collectionId, itemIds);

    public IEnumerable<BaseItem> CollapseItemsWithinBoxSets(
        IEnumerable<BaseItem> items,
        User user
    ) => inner.CollapseItemsWithinBoxSets(items, user);

    public Task<Folder?> GetCollectionsFolder(bool createIfNeeded) =>
        inner.GetCollectionsFolder(createIfNeeded);
}
