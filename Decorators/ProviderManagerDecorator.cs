using Jellyfin.Data.Events;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators;

public sealed class ProviderManagerDecorator(
    IProviderManager inner,
    IApplicationPaths appPaths,
    ILogger<ProviderManagerDecorator> log
) : IProviderManager
{
    /// <summary>
    /// Intercept all HTTP image saves. Creates a zero-byte placeholder at a fake
    /// local path so IsLocalFile=true and ValidateImages passes. Writes the real URL
    /// to a {fakePath}.url sidecar file so ImageResourceFilter can proxy it.
    /// Width/Height/DateModified are set so ImageNeedsRefresh returns false.
    /// </summary>
    public Task SaveImage(
        BaseItem item,
        string url,
        ImageType type,
        int? imageIndex,
        CancellationToken cancellationToken
    )
    {
        if (url.IsUrl())
        {
            log.LogDebug(
                "SaveImage intercepted: item={Id} name={Name} type={ImageType} index={Index} url={Url}",
                item.Id,
                item.Name,
                type,
                imageIndex,
                url
            );

            // Always persist the URL at the gelato fake path so it can be resolved on demand
            // regardless of LazyImages mode — this is the permanent source-of-truth for the URL.
            var info = BuildImageInfo(appPaths, item.Id, type, imageIndex);
            File.WriteAllText(info.Path + ".url", url);

            if (GelatoPlugin.Instance?.Configuration.LazyImages == true)
            {
                // Lazy mode: point the item at the zero-byte placeholder; ImageProcessorDecorator
                // will download on first render.
                item.SetImage(info, imageIndex ?? 0);
                return Task.CompletedTask;
            }

            // Eager mode: let Jellyfin download the image immediately.
            // The .url sidecar above ensures ImageProcessorDecorator can recover the image
            // on demand if the file is later missing (e.g. user deleted it, or mode switched).
        }

        return inner.SaveImage(item, url, type, imageIndex, cancellationToken);
    }

    /// <summary>
    /// Bypasses the decorator and calls Jellyfin's real SaveImage directly.
    /// Used by ImageProcessorDecorator to eagerly download a missing image on demand.
    /// </summary>
    public Task SaveImageDirect(
        BaseItem item,
        string url,
        ImageType type,
        int? imageIndex,
        CancellationToken cancellationToken
    ) => inner.SaveImage(item, url, type, imageIndex, cancellationToken);

    public static void SetRemoteImage(
        IApplicationPaths appPaths,
        BaseItem item,
        ImageType type,
        int? imageIndex,
        string url
    )
    {
        var info = BuildImageInfo(appPaths, item.Id, type, imageIndex);
        // Store the remote URL in a sidecar file next to the placeholder.
        // ImageResourceFilter reads this file to proxy the image.
        File.WriteAllText(info.Path + ".url", url);
        item.SetImage(info, imageIndex ?? 0);
    }

    public static ItemImageInfo BuildImageInfo(
        IApplicationPaths appPaths,
        Guid itemId,
        ImageType type,
        int? imageIndex
    )
    {
        var fileName = imageIndex is > 0 ? $"{type}_{imageIndex}.jpg" : $"{type}.jpg";
        var fakePath = Path.Combine(
            appPaths.DataPath,
            "gelato",
            "images",
            itemId.ToString("N"),
            fileName
        );

        Directory.CreateDirectory(Path.GetDirectoryName(fakePath)!);
        if (!File.Exists(fakePath))
            File.WriteAllBytes(fakePath, Array.Empty<byte>());

        return new ItemImageInfo
        {
            Type = type,
            Path = fakePath,
            Width = 2,
            Height = 3,
            BlurHash = "L00000fQfQ00fQfQfQfQ~qj[j[fQ",
            DateModified = new FileInfo(fakePath).LastWriteTimeUtc,
        };
    }

    // — pass-through for everything else —

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

    public Task SaveImage(
        BaseItem item,
        Stream source,
        string mimeType,
        ImageType type,
        int? imageIndex,
        CancellationToken cancellationToken
    ) => inner.SaveImage(item, source, mimeType, type, imageIndex, cancellationToken);

    public Task SaveImage(
        BaseItem item,
        string source,
        string mimeType,
        ImageType type,
        int? imageIndex,
        bool? saveLocallyWithMedia,
        CancellationToken cancellationToken
    ) => inner.SaveImage(item, source, mimeType, type, imageIndex, saveLocallyWithMedia, cancellationToken);

    public Task SaveImage(Stream source, string mimeType, string path) =>
        inner.SaveImage(source, mimeType, path);

    public void QueueRefresh(Guid itemId, MetadataRefreshOptions options, RefreshPriority priority) =>
        inner.QueueRefresh(itemId, options, priority);

    public Task RefreshFullItem(BaseItem item, MetadataRefreshOptions options, CancellationToken cancellationToken) =>
        inner.RefreshFullItem(item, options, cancellationToken);

    public Task<ItemUpdateType> RefreshSingleItem(BaseItem item, MetadataRefreshOptions options, CancellationToken cancellationToken) =>
        inner.RefreshSingleItem(item, options, cancellationToken);

    public void AddParts(
        IEnumerable<IImageProvider> imageProviders,
        IEnumerable<IMetadataService> metadataServices,
        IEnumerable<IMetadataProvider> metadataProviders,
        IEnumerable<IMetadataSaver> metadataSavers,
        IEnumerable<IExternalId> externalIds,
        IEnumerable<IExternalUrlProvider> externalUrlProviders
    ) => inner.AddParts(imageProviders, metadataServices, metadataProviders, metadataSavers, externalIds, externalUrlProviders);

    public Task<IEnumerable<RemoteImageInfo>> GetAvailableRemoteImages(BaseItem item, RemoteImageQuery query, CancellationToken cancellationToken) =>
        inner.GetAvailableRemoteImages(item, query, cancellationToken);

    public IEnumerable<ImageProviderInfo> GetRemoteImageProviderInfo(BaseItem item) =>
        inner.GetRemoteImageProviderInfo(item);

    public IEnumerable<IImageProvider> GetImageProviders(BaseItem item, ImageRefreshOptions refreshOptions) =>
        inner.GetImageProviders(item, refreshOptions);

    public IEnumerable<IMetadataProvider<T>> GetMetadataProviders<T>(BaseItem item, LibraryOptions libraryOptions)
        where T : BaseItem => inner.GetMetadataProviders<T>(item, libraryOptions);

    public IEnumerable<IMetadataSaver> GetMetadataSavers(BaseItem item, LibraryOptions libraryOptions) =>
        inner.GetMetadataSavers(item, libraryOptions);

    public MetadataPluginSummary[] GetAllMetadataPlugins() => inner.GetAllMetadataPlugins();

    public IEnumerable<ExternalUrl> GetExternalUrls(BaseItem item) => inner.GetExternalUrls(item);

    public IEnumerable<ExternalIdInfo> GetExternalIdInfos(IHasProviderIds item) =>
        inner.GetExternalIdInfos(item);

    public Task SaveMetadataAsync(BaseItem item, ItemUpdateType updateType) =>
        inner.SaveMetadataAsync(item, updateType);

    public Task SaveMetadataAsync(BaseItem item, ItemUpdateType updateType, IEnumerable<string> savers) =>
        inner.SaveMetadataAsync(item, updateType, savers);

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

    public void OnRefreshProgress(BaseItem item, double progress) => inner.OnRefreshProgress(item, progress);

    public void OnRefreshComplete(BaseItem item) => inner.OnRefreshComplete(item);

    public double? GetRefreshProgress(Guid id) => inner.GetRefreshProgress(id);
}
