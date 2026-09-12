using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Configuration;

namespace Gelato.Decorators;

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
