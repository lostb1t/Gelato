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

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
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

        // Unlink them first: deleting a linked version makes Jellyfin save its movie once per row.
        foreach (
            var group in streams
                .Where(v => v.PrimaryVersionId.HasValue)
                .GroupBy(v => v.PrimaryVersionId!.Value)
        )
        {
            if (libraryManager.GetItemById(group.Key) is Video primary)
            {
                var ids = group.Select(v => v.Id).ToHashSet();
                primary.LinkedAlternateVersions = primary
                    .LinkedAlternateVersions.Where(l => l.ItemId is not { } id || !ids.Contains(id))
                    .ToArray();
                persistence.SaveItems([primary], cancellationToken);
            }

            foreach (var stream in group)
            {
                stream.SetPrimaryVersionId(null);
            }
        }

        // Their watch state is on the movie/episode (StreamUserDataSync). Deleted items park their
        // user data under their keys, which rows share with the movie, so clear it first instead
        // of leaving a stale copy that could be handed to another item with the same keys.
        manager.ForgetWatchState(streams, cancellationToken);

        var total = streams.Length;

        var done = 0;

        foreach (var item in streams)
        {
            cancellationToken.ThrowIfCancellationRequested();

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
            var pct = Math.Min(100.0, ((double)done / total) * 100.0);
            progress?.Report(pct);
        }

        progress?.Report(100.0);
        manager.ClearCache();

        log.LogInformation("stream purge completed");
        return Task.CompletedTask;
    }
}
