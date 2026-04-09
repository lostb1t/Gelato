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

    private bool IsGelatoPath(string path) =>
        path.StartsWith(GelatoImagesDir, StringComparison.OrdinalIgnoreCase);

    // For gelato fake paths, return a hardcoded blurhash so Jellyfin never
    // tries to decode the zero-byte placeholder file.
    public string GetImageBlurHash(string path) =>
        IsGelatoPath(path) ? "L00000fQfQ00fQfQfQfQ~qj[j[fQ" : inner.GetImageBlurHash(path);

    public string GetImageBlurHash(string path, ImageDimensions imageDimensions) =>
        IsGelatoPath(path)
            ? "L00000fQfQ00fQfQfQfQ~qj[j[fQ"
            : inner.GetImageBlurHash(path, imageDimensions);

    // On first access, lazily download the remote image to the fake path so
    // inner.ProcessImage can process/resize/cache it normally.
    public async Task<(string Path, string? MimeType, DateTime DateModified)> ProcessImage(
        ImageProcessingOptions options
    )
    {
        var imagePath = options.Image?.Path;
        if (imagePath is not null && IsGelatoPath(imagePath) && new FileInfo(imagePath).Length == 0)
        {
            var urlFile = imagePath + ".url";
            if (File.Exists(urlFile))
            {
                var url = await File.ReadAllTextAsync(urlFile).ConfigureAwait(false);
                await DownloadToFileAsync(url, imagePath).ConfigureAwait(false);
                log.LogInformation(
                    "ImageProcessor: downloaded gelato image to {Path} from {Url}",
                    imagePath,
                    url
                );
            }
        }

        return await inner.ProcessImage(options).ConfigureAwait(false);
    }

    private async Task DownloadToFileAsync(string url, string destPath)
    {
        var client = http.CreateClient();
        using var response = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var file = new FileStream(
            destPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None
        );
        await stream.CopyToAsync(file).ConfigureAwait(false);
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
