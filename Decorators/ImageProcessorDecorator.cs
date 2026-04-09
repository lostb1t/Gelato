using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators;

public sealed class ImageProcessorDecorator(
    IImageProcessor inner,
    IApplicationPaths appPaths,
    IHttpClientFactory http,
    ILogger<ImageProcessorDecorator> log
) : IImageProcessor
{
    private string GelatoImagesDir => Path.Combine(appPaths.DataPath, "gelato", "images");

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
        if (GelatoPlugin.Instance?.Configuration.LazyImages == true && imagePath is not null)
        {
            var fi = new FileInfo(imagePath);
            if (!fi.Exists || fi.Length == 0)
            {
                var urlFile = ResolveUrlFile(imagePath, options);
                if (urlFile is not null)
                {
                    var url = (await File.ReadAllTextAsync(urlFile).ConfigureAwait(false)).Trim();
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);
                        await DownloadToFileAsync(url, imagePath).ConfigureAwait(false);
                        // Persist sidecar at the actual path so future downloads skip the fallback.
                        if (urlFile != imagePath + ".url")
                            File.WriteAllText(imagePath + ".url", url);
                        log.LogDebug(
                            "ImageProcessor: downloaded image to {Path} from {Url}",
                            imagePath,
                            url
                        );
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "ImageProcessor: download failed for {Url}", url);
                    }
                }
            }
        }

        return await inner.ProcessImage(options).ConfigureAwait(false);
    }

    // Returns the .url sidecar path to use, checking the image's own path first, then
    // falling back to the gelato fake path for this item + image type.
    private string? ResolveUrlFile(string imagePath, ImageProcessingOptions options)
    {
        var direct = imagePath + ".url";
        if (File.Exists(direct))
            return direct;

        if (options.Item is null || options.Image is null)
            return null;

        var type = options.Image.Type;
        var index = options.ImageIndex;
        var fileName = index > 0 ? $"{type}_{index}.jpg" : $"{type}.jpg";
        var fallback =
            Path.Combine(GelatoImagesDir, options.Item.Id.ToString("N"), fileName) + ".url";
        return File.Exists(fallback) ? fallback : null;
    }

    private async Task DownloadToFileAsync(string url, string destPath)
    {
        const int maxAttempts = 3;
        var client = http.CreateClient();
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var response = await client
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var stream = await response
                    .Content.ReadAsStreamAsync()
                    .ConfigureAwait(false);
                await using var file = new FileStream(
                    destPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None
                );
                await stream.CopyToAsync(file).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                log.LogDebug(
                    ex,
                    "ImageProcessor: download attempt {Attempt}/{Max} failed for {Url}, retrying...",
                    attempt,
                    maxAttempts,
                    url
                );
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt))).ConfigureAwait(false);
            }
        }
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
