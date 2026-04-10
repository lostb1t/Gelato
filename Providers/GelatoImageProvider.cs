using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Gelato.Providers;

public sealed class GelatoImageProvider(ILogger<GelatoImageProvider> log)
    : IRemoteImageProvider,
        IHasOrder
{
    public string Name => "Gelato";
    public int Order => 0;

    public bool Supports(BaseItem item) => item is Movie or Series;

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) =>
        [ImageType.Primary, ImageType.Backdrop, ImageType.Logo];

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(
        BaseItem item,
        CancellationToken cancellationToken
    )
    {
        var id = ResolveId(item);
        if (id is null)
        {
            log.LogDebug("GelatoImageProvider: no usable ID for {Name}", item.Name);
            return [];
        }

        var stremio = GelatoPlugin.Instance?.Configuration.Stremio;
        if (stremio is null)
            return [];

        var mediaType = item is Movie ? StremioMediaType.Movie : StremioMediaType.Series;
        StremioMeta? meta;
        try
        {
            meta =
                stremio.GetCachedMeta(id)
                ?? await stremio.GetMetaAsync(id, mediaType).ConfigureAwait(false);

            if (meta is not null)
                stremio.CacheMeta(id, meta);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "GelatoImageProvider: failed to fetch meta for {Id}", id);
            return [];
        }

        if (meta is null || !meta.IsValid())
            return [];

        return BuildImages(meta);
    }

    public Task<HttpResponseMessage> GetImageResponse(
        string url,
        CancellationToken cancellationToken
    ) => throw new NotImplementedException();

    private static IEnumerable<RemoteImageInfo> BuildImages(StremioMeta meta)
    {
        var images = new List<RemoteImageInfo>();

        if (!string.IsNullOrWhiteSpace(meta.Poster))
            images.Add(
                new RemoteImageInfo
                {
                    ProviderName = "Gelato",
                    Type = ImageType.Primary,
                    Url = meta.Poster,
                }
            );

        if (!string.IsNullOrWhiteSpace(meta.Background))
            images.Add(
                new RemoteImageInfo
                {
                    ProviderName = "Gelato",
                    Type = ImageType.Backdrop,
                    Url = meta.Background,
                }
            );

        if (!string.IsNullOrWhiteSpace(meta.Logo))
            images.Add(
                new RemoteImageInfo
                {
                    ProviderName = "Gelato",
                    Type = ImageType.Logo,
                    Url = meta.Logo,
                }
            );

        return images;
    }

    private static string? ResolveId(BaseItem item)
    {
        var imdb = item.GetProviderId(MetadataProvider.Imdb);
        if (!string.IsNullOrWhiteSpace(imdb))
            return imdb;

        var tmdb = item.GetProviderId(MetadataProvider.Tmdb);
        if (!string.IsNullOrWhiteSpace(tmdb))
            return $"tmdb:{tmdb}";

        return null;
    }
}
