using Gelato.Decorators;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>
/// Adopts the stream rows an older Gelato synced, once, after startup: they become versions of
/// their movie/episode with the owner, links and watch state a sync would give them, and get a
/// refresh stamp.
/// </summary>
/// <remarks>
/// Until a row is adopted, Jellyfin does not know it as a version: it is counted with the library,
/// can turn up in Next Up, and playback through it saves its state on the row alone. The sync
/// adopts rows when a title is opened; this does it for every title before anybody has to. The
/// stamp matters on its own: a library scan gives every child without a refresh date its first
/// metadata refresh, which saves the row again and would bring back one a purge deleted meanwhile.
/// </remarks>
public sealed class LegacyRowAdoptionService(
    GelatoItemRepository repo,
    IItemPersistenceService persistence,
    GelatoManager manager,
    ILogger<LegacyRowAdoptionService> log
) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => RunAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RunAsync(CancellationToken ct)
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
                .Where(v => v.HasStreamTag())
                .ToList();

            var adopted = 0;
            var titles = 0;
            var orphans = 0;
            // Per title: the rows share the Stremio id of their movie/episode.
            foreach (
                var group in rows.Where(v => v.PrimaryVersionId is null)
                    .GroupBy(v => (Kind: v.GetBaseItemKind(), Id: v.GetProviderId("Stremio") ?? ""))
            )
            {
                ct.ThrowIfCancellationRequested();
                if (group.Key.Id.Length == 0)
                {
                    orphans += group.Count();
                    continue;
                }

                var primaries = repo.GetItemList(
                        new InternalItemsQuery
                        {
                            IncludeItemTypes = [group.Key.Kind],
                            HasAnyProviderId = new Dictionary<string, string>
                            {
                                { "Stremio", group.Key.Id },
                            },
                            Recursive = true,
                            IsDeadPerson = true,
                        }
                    )
                    .OfType<Video>()
                    .Where(v => !v.HasStreamTag() && v.PrimaryVersionId is null)
                    .ToList();
                if (primaries.Count == 0)
                {
                    orphans += group.Count();
                    continue;
                }

                // The same title can exist more than once (a local copy, per-user folders): a row
                // goes to the copy in its own folder, like the sync puts it there.
                foreach (
                    var byPrimary in group.GroupBy(row =>
                        primaries.FirstOrDefault(p => p.ParentId == row.ParentId) ?? primaries[0]
                    )
                )
                {
                    var primary = byPrimary.Key;
                    var mine = byPrimary.ToList();
                    await manager
                        .RunExclusiveAsync(
                            primary.Id,
                            token => manager.AdoptLegacyRows(primary, mine, token),
                            ct
                        )
                        .ConfigureAwait(false);
                    adopted += mine.Count;
                    titles++;
                }
            }

            // Rows already owned but without a stamp (a build that linked but did not stamp).
            var unstamped = rows.Where(v =>
                    v.PrimaryVersionId is not null && v.DateLastRefreshed == DateTime.MinValue
                )
                .ToList();
            if (unstamped.Count > 0)
            {
                var now = DateTime.UtcNow;
                foreach (var row in unstamped)
                {
                    row.DateLastRefreshed = now;
                    row.DateLastSaved = now;
                }

                persistence.SaveItems(unstamped, ct);
            }

            if (adopted > 0 || unstamped.Count > 0 || orphans > 0)
            {
                log.LogInformation(
                    "Legacy stream rows: {Adopted} adopted for {Titles} title(s), {Stamped} stamped, {Orphans} without a title",
                    adopted,
                    titles,
                    unstamped.Count,
                    orphans
                );
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not adopt the stream rows from an older Gelato");
        }
    }
}
