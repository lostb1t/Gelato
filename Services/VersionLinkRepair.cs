using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>
/// Restores the version links of a Gelato movie/episode that Jellyfin saved without them. The
/// refresh queued by an insert loads the series' episodes (or the movie) before their streams are
/// synced and saves them afterwards, with the empty link list they were loaded with: the rows stay
/// owned, but the title lists no stream until something reads its sources
/// (<see cref="GelatoManager.RelinkOwnedRows"/>). Gelato's own writes, which may unlink on
/// purpose, go to the database directly and raise no event.
/// </summary>
public sealed class VersionLinkRepair(
    ILibraryManager libraryManager,
    GelatoManager manager,
    ILogger<VersionLinkRepair> log
) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        libraryManager.ItemUpdated += OnItemUpdated;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        libraryManager.ItemUpdated -= OnItemUpdated;
        return Task.CompletedTask;
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (
            e.Item is not Video { PrimaryVersionId: null, LinkedAlternateVersions.Length: 0 } video
            || video.IsStreamRow()
            || !video.IsGelatoPlaybackItem()
        )
        {
            return;
        }

        try
        {
            manager.RelinkOwnedRows(video);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Could not restore the version links of {Id}", video.Id);
        }
    }
}
