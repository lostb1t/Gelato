using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Configuration;
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

/// <summary>
/// Keeps Jellyfin's trickplay generation away from Gelato's items.
/// </summary>
/// <remarks>
/// The "Generate Trickplay Images" task and library scans refresh trickplay for every non-virtual
/// video. Jellyfin only skips items without media streams, so a stream row that has been probed
/// passes, and trickplay then checks <c>File.Exists</c> on its URL and logs
/// "Media not found at {Path}" as a warning, once per configured width on every run. That URL
/// carries the debrid API key. Trickplay cannot be extracted from a remote stream anyway, and
/// with extraction disabled the same refresh would delete any trickplay data the item has.
/// Local files with a Stremio id are passed through.
/// </remarks>
public sealed class TrickplayManagerDecorator(ITrickplayManager inner) : ITrickplayManager
{
    public Task RefreshTrickplayDataAsync(
        Video video,
        bool replace,
        LibraryOptions libraryOptions,
        CancellationToken cancellationToken
    ) =>
        video.IsGelatoPlaybackItem()
            ? Task.CompletedTask
            : inner.RefreshTrickplayDataAsync(video, replace, libraryOptions, cancellationToken);

    public Task MoveGeneratedTrickplayDataAsync(
        Video video,
        LibraryOptions libraryOptions,
        CancellationToken cancellationToken
    ) =>
        video.IsGelatoPlaybackItem()
            ? Task.CompletedTask
            : inner.MoveGeneratedTrickplayDataAsync(video, libraryOptions, cancellationToken);

    public TrickplayInfo CreateTiles(
        IReadOnlyList<string> images,
        int width,
        TrickplayOptions options,
        string outputDir
    ) => inner.CreateTiles(images, width, options, outputDir);

    public Task<Dictionary<int, TrickplayInfo>> GetTrickplayResolutions(Guid itemId) =>
        inner.GetTrickplayResolutions(itemId);

    public Task<IReadOnlyList<TrickplayInfo>> GetTrickplayItemsAsync(int limit, int offset) =>
        inner.GetTrickplayItemsAsync(limit, offset);

    public Task SaveTrickplayInfo(TrickplayInfo info) => inner.SaveTrickplayInfo(info);

    public Task DeleteTrickplayDataAsync(Guid itemId, CancellationToken cancellationToken) =>
        inner.DeleteTrickplayDataAsync(itemId, cancellationToken);

    public Task<Dictionary<string, Dictionary<int, TrickplayInfo>>> GetTrickplayManifest(
        BaseItem item
    ) => inner.GetTrickplayManifest(item);

    public Task<string> GetTrickplayTilePathAsync(
        BaseItem item,
        int width,
        int index,
        bool saveWithMedia
    ) => inner.GetTrickplayTilePathAsync(item, width, index, saveWithMedia);

    public string GetTrickplayDirectory(
        BaseItem item,
        int tileWidth,
        int tileHeight,
        int width,
        bool saveWithMedia = false
    ) => inner.GetTrickplayDirectory(item, tileWidth, tileHeight, width, saveWithMedia);

    public Task<string?> GetHlsPlaylist(Guid itemId, int width, string? apiKey) =>
        inner.GetHlsPlaylist(itemId, width, apiKey);
}
