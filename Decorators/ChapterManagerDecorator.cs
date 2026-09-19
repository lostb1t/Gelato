using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;

namespace Gelato.Decorators;

/// <summary>
/// Keeps Jellyfin's chapter image extraction away from Gelato's items.
/// </summary>
/// <remarks>
/// The "Extract chapter images" task refreshes chapter images for every non-virtual video. A
/// stream row that has been probed has chapters and a video stream, so Jellyfin runs ffmpeg
/// against its debrid URL once per chapter, and when that fails it writes the path, URL and API
/// key included, to <c>cache/chapter-failures.txt</c>. Gelato's own probe is not affected because
/// it marks the item as a shortcut while it runs.
///
/// The inner call still runs, with <c>extractImages</c> off: Jellyfin then runs no ffmpeg,
/// reports success and only clears image paths that no longer exist. Local files with a Stremio
/// id are passed through.
/// </remarks>
public sealed class ChapterManagerDecorator(IChapterManager inner) : IChapterManager
{
    public bool Supports(BaseItem item) => inner.Supports(item);

    public void SaveChapters(BaseItem item, IReadOnlyList<ChapterInfo> chapters) =>
        inner.SaveChapters(item, chapters);

    public ChapterInfo? GetChapter(Guid baseItemId, int index) =>
        inner.GetChapter(baseItemId, index);

    public IReadOnlyList<ChapterInfo> GetChapters(Guid baseItemId) => inner.GetChapters(baseItemId);

    public Task<bool> RefreshChapterImages(
        Video video,
        IDirectoryService directoryService,
        IReadOnlyList<ChapterInfo> chapters,
        bool extractImages,
        bool saveChapters,
        CancellationToken cancellationToken
    ) =>
        inner.RefreshChapterImages(
            video,
            directoryService,
            chapters,
            extractImages && !video.IsGelatoPlaybackItem(),
            saveChapters,
            cancellationToken
        );

    public Task DeleteChapterDataAsync(Guid itemId, CancellationToken cancellationToken) =>
        inner.DeleteChapterDataAsync(itemId, cancellationToken);
}
