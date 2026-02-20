using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators;

public sealed class PlaylistManagerDecorator(
    IPlaylistManager inner,
    Lazy<GelatoManager> manager,
    ILibraryManager libraryManager,
    ILogger<PlaylistManagerDecorator> log)
    : IPlaylistManager {
    public Playlist GetPlaylistForUser(Guid playlistId, Guid userId) =>
        inner.GetPlaylistForUser(playlistId, userId);

    public Task<PlaylistCreationResult> CreatePlaylist(PlaylistCreationRequest request) =>
        inner.CreatePlaylist(request);

    public Task UpdatePlaylist(PlaylistUpdateRequest request) =>
        inner.UpdatePlaylist(request);

    public IEnumerable<Playlist> GetPlaylists(Guid userId) =>
        inner.GetPlaylists(userId);

    public Task AddUserToShares(PlaylistUserUpdateRequest request) =>
        inner.AddUserToShares(request);

    public Task RemoveUserFromShares(Guid playlistId, Guid userId, PlaylistUserPermissions share) =>
        inner.RemoveUserFromShares(playlistId, userId, share);

    public async Task AddItemToPlaylistAsync(Guid playlistId, IReadOnlyCollection<Guid> itemIds, Guid userId) {
        await inner.AddItemToPlaylistAsync(playlistId, itemIds, userId).ConfigureAwait(false);

        if (libraryManager.GetItemById(playlistId) is not Playlist playlist)
            return;

        var addedItems = itemIds
            .Select(libraryManager.GetItemById)
            .Where(item => item is not null)
            .ToList();

        var gelatoItems = addedItems.Where(manager.Value.IsGelato).ToList();
        if (gelatoItems.Count == 0)
            return;

        var needsFix = false;

        for (int i = 0; i < playlist.LinkedChildren.Length; i++) {
            var linkedChild = playlist.LinkedChildren[i];

            if (
                string.IsNullOrEmpty(linkedChild.LibraryItemId)
                && !string.IsNullOrEmpty(linkedChild.Path)
            ) {
                var matchingItem = gelatoItems.FirstOrDefault(item =>
                    item.Path == linkedChild.Path
                );

                if (matchingItem != null) {
                    log.LogDebug(
                        "Fixing Gelato LinkedChild with path {Path} for item {Id}",
                        linkedChild.Path,
                        matchingItem.Id
                    );

                    playlist.LinkedChildren[i] = new LinkedChild {
                        LibraryItemId = matchingItem.Id.ToString("N", CultureInfo.InvariantCulture),
                        Type = LinkedChildType.Manual,
                    };
                    needsFix = true;
                }
            }
        }

        if (needsFix) {
            log.LogDebug(
                "Fixing {Count} Gelato items in playlist {Name}",
                gelatoItems.Count,
                playlist.Name
            );
            await playlist
                .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    public Task RemoveItemFromPlaylistAsync(string playlistId, IEnumerable<string> entryIds) =>
        inner.RemoveItemFromPlaylistAsync(playlistId, entryIds);

    public Folder GetPlaylistsFolder() =>
        inner.GetPlaylistsFolder();

    public Folder GetPlaylistsFolder(Guid userId) =>
        inner.GetPlaylistsFolder(userId);

    public Task MoveItemAsync(string playlistId, string entryId, int newIndex, Guid callingUserId) =>
        inner.MoveItemAsync(playlistId, entryId, newIndex, callingUserId);

    public Task RemovePlaylistsAsync(Guid userId) =>
        inner.RemovePlaylistsAsync(userId);

    public void SavePlaylistFile(Playlist item) =>
        inner.SavePlaylistFile(item);
}
