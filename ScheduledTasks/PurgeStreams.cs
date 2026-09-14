using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.ScheduledTasks;

public sealed class PurgeGelatoStreamsTask(
    ILibraryManager libraryManager,
    IItemPersistenceService persistence,
    ILogger<PurgeGelatoStreamsTask> log,
    GelatoManager manager
) : IScheduledTask
{
    public string Name => "Purge streams";
    public string Key => "PurgeGelatoStreamsTask";
    public string Description => "Removes all stremio streams";
    public string Category => "Gelato Maintenance";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromDays(7).Ticks,
            },
        ];
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        log.LogInformation("purging streams");

        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode],
            Recursive = true,
            HasAnyProviderId = new Dictionary<string, string>
            {
                { "Stremio", string.Empty },
                { "stremio", string.Empty },
            },
            IsDeadPerson = true,
            // Stream rows are alternate versions, which Jellyfin leaves out of queries by default.
            IncludeOwnedItems = true,
        };

        var streams = libraryManager
            .GetItemList(query)
            .OfType<Video>()
            .Where(v => v.IsStream())
            .ToArray();

        var total = streams.Length;
        var done = 0;

        // Per movie/episode, as its only writer: a sync of the same item running at the same time
        // would save the rows back. Rows never linked to an item go by themselves.
        foreach (var group in streams.GroupBy(v => v.PrimaryVersionId ?? v.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rows = group.ToArray();

            await manager
                .RunExclusiveAsync(
                    group.Key,
                    ct =>
                    {
                        // Unlink them first: deleting a linked version makes Jellyfin save its
                        // movie once per row.
                        if (
                            rows[0].PrimaryVersionId.HasValue
                            && libraryManager.GetItemById(group.Key) is Video primary
                        )
                        {
                            // Playlist and collection entries that name a row move to the movie.
                            manager.RerouteLinks(rows, primary.Id);

                            var ids = rows.Select(v => v.Id).ToHashSet();
                            primary.LinkedAlternateVersions = primary
                                .LinkedAlternateVersions.Where(l =>
                                    l.ItemId is not { } id || !ids.Contains(id)
                                )
                                .ToArray();
                            persistence.SaveItems([primary], ct);
                        }

                        foreach (var stream in rows)
                        {
                            stream.SetPrimaryVersionId(null);
                        }

                        // Their watch state is on the movie/episode (StreamUserDataSync). Deleted
                        // items park their user data under their keys, which rows share with the
                        // movie, so clear it first instead of leaving a stale copy that could be
                        // handed to another item with the same keys.
                        manager.ForgetWatchState(rows, ct);

                        foreach (var item in rows)
                        {
                            ct.ThrowIfCancellationRequested();

                            try
                            {
                                libraryManager.DeleteItem(
                                    item,
                                    new DeleteOptions { DeleteFileLocation = true },
                                    true
                                );
                            }
                            catch (Exception ex)
                            {
                                log.LogWarning(ex, "Failed to delete item {ItemId}", item.Id);
                            }

                            done++;
                            progress?.Report(Math.Min(100.0, 100.0 * done / total));
                        }

                        return Task.CompletedTask;
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
        }

        progress?.Report(100.0);
        manager.ClearCache();

        log.LogInformation("stream purge completed");
    }
}
