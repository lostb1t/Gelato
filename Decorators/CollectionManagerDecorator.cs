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

    /// <summary>
    /// A new collection made from a version's page holds the movie/episode, like an existing one
    /// it is added to.
    /// </summary>
    public Task<BoxSet> CreateCollectionAsync(CollectionCreationOptions options)
    {
        options.ItemIdList = options
            .ItemIdList.Select(id =>
                Guid.TryParse(id, out var guid) && libraryManager.GetItemById(guid) is { } item
                    ? item.PrimaryVersionOrSelf(libraryManager).Id.ToString("N")
                    : id
            )
            .Distinct()
            .ToList();
        return inner.CreateCollectionAsync(options);
    }

    public async Task AddToCollectionAsync(Guid collectionId, IEnumerable<Guid> itemIds)
    {
        if (libraryManager.GetItemById(collectionId) is not BoxSet collection)
            throw new ArgumentException(
                "No collection exists with the supplied collectionId " + collectionId
            );

        List<BaseItem>? itemList = null;
        var linkedChildrenList = collection.GetLinkedChildren();
        var currentLinkedChildrenIds = linkedChildrenList.Select(i => i.Id).ToList();

        foreach (var requestedId in itemIds)
        {
            // A version's page adds the movie/episode it is a version of.
            var item = (
                libraryManager.GetItemById(requestedId)
                ?? throw new ArgumentException("No item exists with the supplied Id " + requestedId)
            ).PrimaryVersionOrSelf(libraryManager);
            var id = item.Id;

            if (!currentLinkedChildrenIds.Contains(id) && !item.IsStream())
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
            newChildren[originalLen + i] = LinkedChild.Create(item);

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

    /// <summary>
    /// Collections contain the movie/episode, never its stream rows. Jellyfin 12 clients show a
    /// picked version as the page item, so look its collections up on the movie.
    /// </summary>
    public IEnumerable<BoxSet> GetCollectionsContainingItem(User user, Guid itemId) =>
        inner.GetCollectionsContainingItem(
            user,
            libraryManager.GetItemById(itemId) is { } item
                ? item.PrimaryVersionOrSelf(libraryManager).Id
                : itemId
        );
}
