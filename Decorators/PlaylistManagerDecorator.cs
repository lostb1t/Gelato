using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators;

public sealed class PlaylistManagerDecorator : IPlaylistManager {
    private readonly IPlaylistManager _inner;
    private readonly Lazy<GelatoManager> _manager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PlaylistManagerDecorator> _log;

    public PlaylistManagerDecorator(
        IPlaylistManager inner,
        Lazy<GelatoManager> manager,
        ILibraryManager libraryManager,
        ILogger<PlaylistManagerDecorator> log
    ) {
        _inner = inner;
        _manager = manager;
        _libraryManager = libraryManager;
        _log = log;
    }

    public Playlist GetPlaylistForUser(Guid playlistId, Guid userId) =>
        _inner.GetPlaylistForUser(playlistId, userId);

    public Task<PlaylistCreationResult> CreatePlaylist(PlaylistCreationRequest request) =>
        _inner.CreatePlaylist(request);

    public Task UpdatePlaylist(PlaylistUpdateRequest request) =>
        _inner.UpdatePlaylist(request);

    public IEnumerable<Playlist> GetPlaylists(Guid userId) =>
        _inner.GetPlaylists(userId);

    public Task AddUserToShares(PlaylistUserUpdateRequest request) =>
        _inner.AddUserToShares(request);

    public Task RemoveUserFromShares(Guid playlistId, Guid userId, PlaylistUserPermissions share) =>
        _inner.RemoveUserFromShares(playlistId, userId, share);

    public async Task AddItemToPlaylistAsync(Guid playlistId, IReadOnlyCollection<Guid> itemIds, Guid userId) {
        await _inner.AddItemToPlaylistAsync(playlistId, itemIds, userId).ConfigureAwait(false);

        var playlist = _libraryManager.GetItemById(playlistId) as Playlist;
        if (playlist is null)
            return;

        var addedItems = itemIds
            .Select(id => _libraryManager.GetItemById(id))
            .Where(item => item is not null)
            .ToList();

        var gelatoItems = addedItems.Where(_manager.Value.IsGelato).ToList();
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
                    _log.LogDebug(
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
            _log.LogDebug(
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
        _inner.RemoveItemFromPlaylistAsync(playlistId, entryIds);

    public Folder GetPlaylistsFolder() =>
        _inner.GetPlaylistsFolder();

    public Folder GetPlaylistsFolder(Guid userId) =>
        _inner.GetPlaylistsFolder(userId);

    public Task MoveItemAsync(string playlistId, string entryId, int newIndex, Guid callingUserId) =>
        _inner.MoveItemAsync(playlistId, entryId, newIndex, callingUserId);

    public Task RemovePlaylistsAsync(Guid userId) =>
        _inner.RemovePlaylistsAsync(userId);

    public void SavePlaylistFile(Playlist item) =>
        _inner.SavePlaylistFile(item);
}
