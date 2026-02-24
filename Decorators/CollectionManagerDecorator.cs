using System.Globalization;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators;

public sealed class CollectionManagerDecorator(
    ICollectionManager inner,
    Lazy<GelatoManager> manager,
    ILibraryManager libraryManager,
    IProviderManager providerManager,
    IDirectoryService directoryService,
    ILogger<CollectionManagerDecorator> log
) : ICollectionManager
{
    public event EventHandler<CollectionCreatedEventArgs>? CollectionCreated
    {
        add => inner.CollectionCreated += value;
        remove => inner.CollectionCreated -= value;
    }

    public event EventHandler<CollectionModifiedEventArgs>? ItemsAddedToCollection;

    public event EventHandler<CollectionModifiedEventArgs>? ItemsRemovedFromCollection
    {
        add => inner.ItemsRemovedFromCollection += value;
        remove => inner.ItemsRemovedFromCollection -= value;
    }

    public Task<BoxSet> CreateCollectionAsync(CollectionCreationOptions options) =>
        inner.CreateCollectionAsync(options);

    public async Task AddToCollectionAsync(Guid collectionId, IEnumerable<Guid> itemIds)
    {
        if (libraryManager.GetItemById(collectionId) is not BoxSet collection)
            throw new ArgumentException(
                "No collection exists with the supplied collectionId " + collectionId
            );

        List<BaseItem>? itemList = null;
        var linkedChildrenList = collection.GetLinkedChildren();
        var currentLinkedChildrenIds = linkedChildrenList.Select(i => i.Id).ToList();

        foreach (var id in itemIds)
        {
            var item =
                libraryManager.GetItemById(id)
                ?? throw new ArgumentException("No item exists with the supplied Id " + id);

            if (!currentLinkedChildrenIds.Contains(id))
            {
                (itemList ??= []).Add(item);
                linkedChildrenList.Add(item);
            }
        }

        if (itemList is null)
            return;

        var originalLen = collection.LinkedChildren.Length;
        LinkedChild[] newChildren = new LinkedChild[originalLen + itemList.Count];
        collection.LinkedChildren.CopyTo(newChildren, 0);

        for (var i = 0; i < itemList.Count; i++)
        {
            var item = itemList[i];
            newChildren[originalLen + i] = item.IsGelato()
                ? new LinkedChild
                {
                    LibraryItemId = item.Id.ToString("N", CultureInfo.InvariantCulture),
                    Type = LinkedChildType.Manual,
                }
                : LinkedChild.Create(item);

            log.LogDebug(
                "Adding item {Id} (Gelato={IsGelato}) to collection {Name}",
                item.Id,
                item.IsGelato(),
                collection.Name
            );
        }

        collection.LinkedChildren = newChildren;
        collection.UpdateRatingToItems(linkedChildrenList);

        await collection
            .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
            .ConfigureAwait(false);

        providerManager.QueueRefresh(
            collection.Id,
            new MetadataRefreshOptions(directoryService) { ForceSave = true },
            RefreshPriority.High
        );

        ItemsAddedToCollection?.Invoke(this, new CollectionModifiedEventArgs(collection, itemList));
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
