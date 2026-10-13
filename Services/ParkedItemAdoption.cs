using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;

namespace Gelato.Services;

/// <summary>
/// Puts the Gelato items that lost their folder (<see cref="GelatoManager.MoveOutOf"/>) into the
/// configured one as soon as Jellyfin has made a library or a library folder an item: a library
/// that was renamed or added, a folder that was added to one.
/// </summary>
public sealed class ParkedItemAdoption(ILibraryManager libraryManager, GelatoManager manager)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        libraryManager.ItemAdded += OnItemAdded;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        libraryManager.ItemAdded -= OnItemAdded;
        return Task.CompletedTask;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item?.GetBaseItemKind() is BaseItemKind.Folder or BaseItemKind.CollectionFolder)
            manager.AdoptParkedSoon();
    }
}

/// <summary>
/// The same after every library scan, for whatever the event above did not bring in: a scan is
/// what makes a folder that was added to a library an item.
/// </summary>
public sealed class ParkedItemAdoptionPostScan(GelatoManager manager) : ILibraryPostScanTask
{
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await manager.AdoptParkedAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }
}
