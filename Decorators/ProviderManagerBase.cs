using Jellyfin.Data.Events;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Gelato.Decorators;

public abstract class ProviderManagerBase(IProviderManager inner) : IProviderManager
{
    public event EventHandler<GenericEventArgs<BaseItem>> RefreshStarted
    {
        add => inner.RefreshStarted += value;
        remove => inner.RefreshStarted -= value;
    }

    public event EventHandler<GenericEventArgs<BaseItem>> RefreshCompleted
    {
        add => inner.RefreshCompleted += value;
        remove => inner.RefreshCompleted -= value;
    }

    public event EventHandler<GenericEventArgs<Tuple<BaseItem, double>>> RefreshProgress
    {
        add => inner.RefreshProgress += value;
        remove => inner.RefreshProgress -= value;
    }

    public virtual Task SaveImage(
        BaseItem item,
        string url,
        ImageType type,
        int? imageIndex,
        CancellationToken cancellationToken
    ) => inner.SaveImage(item, url, type, imageIndex, cancellationToken);

    public virtual Task SaveImage(
        BaseItem item,
        Stream source,
        string mimeType,
        ImageType type,
        int? imageIndex,
        CancellationToken cancellationToken
    ) => inner.SaveImage(item, source, mimeType, type, imageIndex, cancellationToken);

    public virtual Task SaveImage(
        BaseItem item,
        string source,
        string mimeType,
        ImageType type,
        int? imageIndex,
        bool? saveLocallyWithMedia,
        CancellationToken cancellationToken
    ) =>
        inner.SaveImage(
            item,
            source,
            mimeType,
            type,
            imageIndex,
            saveLocallyWithMedia,
            cancellationToken
        );

    public virtual Task SaveImage(Stream source, string mimeType, string path) =>
        inner.SaveImage(source, mimeType, path);

    public void QueueRefresh(
        Guid itemId,
        MetadataRefreshOptions options,
        RefreshPriority priority
    ) => inner.QueueRefresh(itemId, options, priority);

    public Task RefreshFullItem(
        BaseItem item,
        MetadataRefreshOptions options,
        CancellationToken cancellationToken
    ) => inner.RefreshFullItem(item, options, cancellationToken);

    public Task<ItemUpdateType> RefreshSingleItem(
        BaseItem item,
        MetadataRefreshOptions options,
        CancellationToken cancellationToken
    ) => inner.RefreshSingleItem(item, options, cancellationToken);

    public void AddParts(
        IEnumerable<IImageProvider> imageProviders,
        IEnumerable<IMetadataService> metadataServices,
        IEnumerable<IMetadataProvider> metadataProviders,
        IEnumerable<IMetadataSaver> metadataSavers,
        IEnumerable<IExternalId> externalIds,
        IEnumerable<IExternalUrlProvider> externalUrlProviders
    ) =>
        inner.AddParts(
            imageProviders,
            metadataServices,
            metadataProviders,
            metadataSavers,
            externalIds,
            externalUrlProviders
        );

    public Task<IEnumerable<RemoteImageInfo>> GetAvailableRemoteImages(
        BaseItem item,
        RemoteImageQuery query,
        CancellationToken cancellationToken
    ) => inner.GetAvailableRemoteImages(item, query, cancellationToken);

    public IEnumerable<ImageProviderInfo> GetRemoteImageProviderInfo(BaseItem item) =>
        inner.GetRemoteImageProviderInfo(item);

    public IEnumerable<IImageProvider> GetImageProviders(
        BaseItem item,
        ImageRefreshOptions refreshOptions
    ) => inner.GetImageProviders(item, refreshOptions);

    public IEnumerable<IMetadataProvider<T>> GetMetadataProviders<T>(
        BaseItem item,
        LibraryOptions libraryOptions
    )
        where T : BaseItem => inner.GetMetadataProviders<T>(item, libraryOptions);

    public IEnumerable<IMetadataSaver> GetMetadataSavers(
        BaseItem item,
        LibraryOptions libraryOptions
    ) => inner.GetMetadataSavers(item, libraryOptions);

    public MetadataPluginSummary[] GetAllMetadataPlugins() => inner.GetAllMetadataPlugins();

    public IEnumerable<ExternalUrl> GetExternalUrls(BaseItem item) => inner.GetExternalUrls(item);

    public IEnumerable<ExternalIdInfo> GetExternalIdInfos(IHasProviderIds item) =>
        inner.GetExternalIdInfos(item);

    public Task SaveMetadataAsync(BaseItem item, ItemUpdateType updateType) =>
        inner.SaveMetadataAsync(item, updateType);

    public Task SaveMetadataAsync(
        BaseItem item,
        ItemUpdateType updateType,
        IEnumerable<string> savers
    ) => inner.SaveMetadataAsync(item, updateType, savers);

    public MetadataOptions GetMetadataOptions(BaseItem item) => inner.GetMetadataOptions(item);

    public Task<IEnumerable<RemoteSearchResult>> GetRemoteSearchResults<TItemType, TLookupType>(
        RemoteSearchQuery<TLookupType> searchInfo,
        CancellationToken cancellationToken
    )
        where TItemType : BaseItem, new()
        where TLookupType : ItemLookupInfo =>
        inner.GetRemoteSearchResults<TItemType, TLookupType>(searchInfo, cancellationToken);

    public HashSet<Guid> GetRefreshQueue() => inner.GetRefreshQueue();

    public void OnRefreshStart(BaseItem item) => inner.OnRefreshStart(item);

    public void OnRefreshProgress(BaseItem item, double progress) =>
        inner.OnRefreshProgress(item, progress);

    public void OnRefreshComplete(BaseItem item) => inner.OnRefreshComplete(item);

    public double? GetRefreshProgress(Guid id) => inner.GetRefreshProgress(id);
}
