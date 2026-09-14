using Gelato.Decorators;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>
/// Gives stream rows synced by an older Gelato a refresh stamp at startup.
/// </summary>
/// <remarks>
/// The sync stamps every row it writes, because a library scan gives each child without a refresh
/// date its first metadata refresh: that saves the row again, which brings back one a purge
/// deleted meanwhile, and moves parked user data with the movie's keys onto it. Rows from before
/// the stamp get theirs on their title's next sync, which for a title nobody opens is never; a scan
/// in the meantime could resurrect rows a purge removed. Stamping them once at startup closes that
/// window without waiting for the visits.
/// </remarks>
public sealed class LegacyRowStampService(
    GelatoItemRepository repo,
    IItemPersistenceService persistence,
    ILogger<LegacyRowStampService> log
) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => StampAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task StampAsync(CancellationToken ct)
    {
        try
        {
            // The library is still coming up when hosted services start.
            await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);

            var rows = repo.GetItemList(
                    new InternalItemsQuery
                    {
                        IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
                        Recursive = true,
                        HasAnyProviderId = new Dictionary<string, string>
                        {
                            { "Stremio", string.Empty },
                            { "stremio", string.Empty },
                        },
                        IsDeadPerson = true,
                        IncludeOwnedItems = true,
                    }
                )
                .OfType<Video>()
                .Where(v => v.HasStreamTag() && v.DateLastRefreshed == DateTime.MinValue)
                .ToList();
            if (rows.Count == 0)
                return;

            var now = DateTime.UtcNow;
            foreach (var row in rows)
            {
                row.DateLastRefreshed = now;
                row.DateLastSaved = now;
            }

            persistence.SaveItems(rows, ct);
            log.LogInformation(
                "Stamped {Count} stream row(s) from an older Gelato with a refresh date",
                rows.Count
            );
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not stamp the stream rows from an older Gelato");
        }
    }
}
