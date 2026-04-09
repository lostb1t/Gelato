using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators;

public sealed class ProviderManagerDecorator(
    IProviderManager inner,
    IApplicationPaths appPaths,
    ILogger<ProviderManagerDecorator> log
) : ProviderManagerBase(inner)
{
    /// <summary>
    /// Intercept all HTTP image saves. Creates a zero-byte placeholder at a fake
    /// local path so IsLocalFile=true and ValidateImages passes. Writes the real URL
    /// to a {fakePath}.url sidecar file so ImageResourceFilter can proxy it.
    /// Width/Height/DateModified are set so ImageNeedsRefresh returns false.
    /// </summary>
    public override Task SaveImage(
        BaseItem item,
        string url,
        ImageType type,
        int? imageIndex,
        CancellationToken cancellationToken
    )
    {
        if (
            url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        )
        {
            log.LogInformation(
                "SaveImage intercepted: item={Id} name={Name} type={ImageType} index={Index} url={Url}",
                item.Id,
                item.Name,
                type,
                imageIndex,
                url
            );

            // Also write a .url sidecar at the item's existing local image path (if any).
            // If Jellyfin reverts the item's path back to the original metadata path,
            // ImageProcessorDecorator can still lazy-download from the sidecar.
            var existing = item.GetImageInfo(type, imageIndex ?? 0);
            if (existing?.IsLocalFile == true && existing.Path is not null)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(existing.Path)!);
                    if (!File.Exists(existing.Path))
                        File.WriteAllBytes(existing.Path, Array.Empty<byte>());
                    File.WriteAllText(existing.Path + ".url", url);
                }
                catch (Exception ex)
                {
                    log.LogWarning(
                        ex,
                        "SaveImage: could not write sidecar at existing path {Path}",
                        existing.Path
                    );
                }
            }

            SetRemoteImage(appPaths, item, type, imageIndex, url);
            return Task.CompletedTask;
        }

        return base.SaveImage(item, url, type, imageIndex, cancellationToken);
    }

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
            BlurHash = "000",
            DateModified = new FileInfo(fakePath).LastWriteTimeUtc,
        };
    }
}
