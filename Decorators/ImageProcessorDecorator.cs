using System.Collections.Concurrent;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators;

public sealed class ImageProcessorDecorator(
    IImageProcessor inner,
    IApplicationPaths appPaths,
    Lazy<ProviderManagerDecorator> providerManager,
    Lazy<ILibraryManager> libraryManager,
    ILogger<ImageProcessorDecorator> log
) : IImageProcessor
{
    // Remote images that answered with an error, and until when they are left alone. A still of an
    // episode that has not aired yet is simply not there yet, and retrying it on every single
    // request only produced the same 404 again (issue 226). A permanent answer is remembered for
    // hours — the still usually appears within days — a transient one only for minutes.
    private static readonly ConcurrentDictionary<string, DateTime> FailedUrls = new(
        StringComparer.Ordinal
    );
    private static readonly TimeSpan PermanentFailureTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan TransientFailureTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Whether the URL failed recently and should not be fetched again yet.
    /// </summary>
    public static bool IsKnownDead(string url)
    {
        if (!FailedUrls.TryGetValue(url, out var until))
            return false;
        if (DateTime.UtcNow < until)
            return true;
        FailedUrls.TryRemove(url, out _);
        return false;
    }

    private static void RememberFailure(string url, Exception ex)
    {
        // A 4xx means the image is not there; anything else (5xx, a timeout, no route) can be over
        // in a moment.
        var permanent =
            ex is HttpRequestException { StatusCode: { } status }
            && (int)status is >= 400 and < 500;
        if (FailedUrls.Count > 4096)
        {
            var now = DateTime.UtcNow;
            foreach (var (key, expiry) in FailedUrls)
            {
                if (expiry <= now)
                    FailedUrls.TryRemove(key, out _);
            }
        }

        FailedUrls[url] = DateTime.UtcNow + (permanent ? PermanentFailureTtl : TransientFailureTtl);
    }

    // Return a hardcoded blurhash for any zero-byte/missing placeholder that has a .url sidecar,
    // so Jellyfin never tries to decode the placeholder file.
    public string GetImageBlurHash(string path)
    {
        if (ShouldUsePlaceholderBlurHash(path))
            return "L00000fQfQ00fQfQfQfQ~qj[j[fQ";
        return inner.GetImageBlurHash(path);
    }

    public string GetImageBlurHash(string path, ImageDimensions imageDimensions)
    {
        if (ShouldUsePlaceholderBlurHash(path))
            return "L00000fQfQ00fQfQfQfQ~qj[j[fQ";
        return inner.GetImageBlurHash(path, imageDimensions);
    }

    private static bool ShouldUsePlaceholderBlurHash(string path)
    {
        if (!File.Exists(path + ".url"))
            return false;
        var fi = new FileInfo(path);
        return !fi.Exists || fi.Length == 0;
    }

    // On first access, lazily download the remote image to the placeholder path so
    // inner.ProcessImage can process/resize/cache it normally.
    // Works for any path that has a .url sidecar (gelato items, persons, studios, etc.).
    // Falls back to the gelato fake path for items whose DB path reverted to the Jellyfin
    // metadata location (e.g. persons scanned before the plugin intercepted their SaveImage).
    public async Task<(string Path, string? MimeType, DateTime DateModified)> ProcessImage(
        ImageProcessingOptions options
    )
    {
        var imagePath = options.Image?.Path;
        if (imagePath is not null && options.Item is not null)
        {
            var fi = new FileInfo(imagePath);
            if (!fi.Exists || fi.Length == 0)
            {
                var urlFile = ResolveUrlFile(imagePath, options);
                if (urlFile is not null)
                {
                    var url = (await File.ReadAllTextAsync(urlFile).ConfigureAwait(false)).Trim();
                    if (IsKnownDead(url))
                    {
                        log.LogDebug(
                            "ImageProcessor: {Url} failed recently, not fetching it again for {Id}",
                            Redact.Url(url),
                            options.Item.Id
                        );
                        throw NotFound(options);
                    }

                    try
                    {
                        await providerManager.Value
                            .SaveImageDirect(
                                options.Item,
                                url,
                                options.Image!.Type,
                                options.ImageIndex,
                                CancellationToken.None
                            )
                            .ConfigureAwait(false);
                        // Re-read the image info — Jellyfin may have saved to a different path.
                        var fresh = options.Item.GetImageInfo(options.Image.Type, options.ImageIndex);
                        if (fresh is not null)
                            options.Image = fresh;
                        // Persist the updated path to the DB so future loads use the real file.
                        await libraryManager.Value
                            .UpdateImagesAsync(options.Item)
                            .ConfigureAwait(false);
                        log.LogDebug(
                            "ImageProcessor: resolved image for {Id} type={Type} from {Url}",
                            options.Item.Id,
                            options.Image.Type,
                            Redact.Url(url)
                        );
                        FailedUrls.TryRemove(url, out _);
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(
                            ex,
                            "ImageProcessor: download failed for {Id} from {Url}",
                            options.Item.Id,
                            Redact.Url(url)
                        );
                        RememberFailure(url, ex);
                    }
                }
                else
                {
                    log.LogWarning(
                        "ImageProcessor: no .url sidecar for {Path} — skipping (metadata refresh needed)",
                        imagePath
                    );
                }

                // Whatever happened above, an image of zero bytes is not an image: served as it
                // is, the endpoint answers 200 and the client draws an empty card instead of its
                // own placeholder, because it cannot tell the two apart.
                var current = new FileInfo(options.Image!.Path);
                if (!current.Exists || current.Length == 0)
                    throw NotFound(options);
            }
        }

        return await inner.ProcessImage(options).ConfigureAwait(false);
    }

    private static FileNotFoundException NotFound(ImageProcessingOptions options) =>
        new($"No image of type {options.Image?.Type} for item {options.Item?.Id}");

    // Returns the .url sidecar path to use, checking the image's own path first, then
    // falling back to the gelato fake path for this item + image type.
    private string? ResolveUrlFile(string imagePath, ImageProcessingOptions options) =>
        options.Item is null || options.Image is null
            ? null
            : ResolveUrlFile(
                appPaths,
                options.Item.Id,
                imagePath,
                options.Image.Type,
                options.ImageIndex
            );

    public static string? ResolveUrlFile(
        IApplicationPaths appPaths,
        Guid itemId,
        string imagePath,
        ImageType type,
        int index
    )
    {
        var direct = imagePath + ".url";
        if (File.Exists(direct))
            return direct;

        var fileName = index > 0 ? $"{type}_{index}.jpg" : $"{type}.jpg";
        var fallback =
            Path.Combine(appPaths.DataPath, "gelato", "images", itemId.ToString("N"), fileName)
            + ".url";
        return File.Exists(fallback) ? fallback : null;
    }

    // — pass-through for everything else —

    public IReadOnlyCollection<string> SupportedInputFormats => inner.SupportedInputFormats;
    public bool SupportsImageCollageCreation => inner.SupportsImageCollageCreation;

    public ImageDimensions GetImageDimensions(string path) => inner.GetImageDimensions(path);

    public ImageDimensions GetImageDimensions(BaseItem item, ItemImageInfo info) =>
        inner.GetImageDimensions(item, info);

    public string? GetImageCacheTag(string baseItemPath, DateTime imageDateModified) =>
        inner.GetImageCacheTag(baseItemPath, imageDateModified);

    public string? GetImageCacheTag(BaseItemDto item, ChapterInfo image) =>
        inner.GetImageCacheTag(item, image);

    public string GetImageCacheTag(BaseItem item, ItemImageInfo image) =>
        inner.GetImageCacheTag(item, image);

    public string GetImageCacheTag(BaseItemDto item, ItemImageInfo image) =>
        inner.GetImageCacheTag(item, image);

    public string? GetImageCacheTag(BaseItem item, ChapterInfo chapter) =>
        inner.GetImageCacheTag(item, chapter);

    public string? GetImageCacheTag(User user) => inner.GetImageCacheTag(user);

    public IReadOnlyCollection<ImageFormat> GetSupportedImageOutputFormats() =>
        inner.GetSupportedImageOutputFormats();

    public void CreateImageCollage(ImageCollageOptions options, string? libraryName) =>
        inner.CreateImageCollage(options, libraryName);
}
