using Gelato.Filters;
using Gelato.Services;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.ScheduledTasks;

/// <summary>
/// Deletes the titles opened from search that nobody played, marked a favourite or put into a
/// collection or playlist within <see cref="Config.PluginConfiguration.RemoveUnplayedSearchTitlesDays"/>
/// days (5) of being opened (<see cref="PendingSearchTitles"/>).
/// </summary>
/// <remarks>
/// Hidden from the task list: <see cref="Config.PluginConfiguration.HideUnplayedSearchTitles"/>
/// switches it, and it does nothing while that is off. A title is kept, and shows in the library again, when anything of the above is found
/// on it, also where the event that should have released it was missed.
/// </remarks>
public sealed class RemoveUnplayedSearchTitlesTask(
    ILibraryManager libraryManager,
    IUserManager userManager,
    PendingSearchTitles pending,
    GelatoManager manager,
    DeleteResourceFilter deleter,
    ILogger<RemoveUnplayedSearchTitlesTask> log
) : IScheduledTask, IConfigurableScheduledTask
{
    public string Name => "Remove unplayed titles opened from search";
    public string Key => "GelatoRemoveUnplayedSearchTitles";

    public string Description =>
        "Deletes titles opened from search that nobody played within 5 days, while Gelato hides them until played.";

    public string Category => "Gelato Maintenance";
    public bool IsHidden => true;
    public bool IsEnabled => true;
    public bool IsLogged => true;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromDays(1).Ticks,
            },
        ];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var cfg = GelatoPlugin.Instance!.Configuration;
        var ids = pending.Ids();
        if (!cfg.HideUnplayedSearchTitles || ids.Length == 0)
            return;

        var days =
            cfg.RemoveUnplayedSearchTitlesDays > 0
                ? cfg.RemoveUnplayedSearchTitlesDays
                : Config.PluginConfiguration.DefaultRemoveUnplayedSearchTitlesDays;
        var cutoff = DateTime.UtcNow.AddDays(-days);
        var listed = ListedIds();
        var users = userManager.GetUsers().ToList();
        var done = 0;
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(100.0 * done++ / ids.Length);

            if (libraryManager.GetItemById(id) is not { } item)
                continue;

            if (item.DateCreated > cutoff)
                continue;

            if (IsKept(item, users, listed))
            {
                log.LogInformation("Keeping {Name} ({Id}): it has been used", item.Name, item.Id);
                pending.Release(id);
                continue;
            }

            log.LogInformation(
                "Removing {Name} ({Id}), opened from search on {Date:d} and never played",
                item.Name,
                item.Id,
                item.DateCreated
            );
            await manager.StopInsertRefreshAsync(item.Id).ConfigureAwait(false);
            await deleter.DeleteItemAsync(item).ConfigureAwait(false);
        }

        progress.Report(100);
    }

    // The items any collection or playlist names.
    private HashSet<Guid> ListedIds() =>
        [
            .. libraryManager
                .GetItemList(
                    new InternalItemsQuery
                    {
                        IncludeItemTypes = [BaseItemKind.BoxSet, BaseItemKind.Playlist],
                        Recursive = true,
                        IsDeadPerson = true,
                    }
                )
                .OfType<Folder>()
                .SelectMany(f => f.LinkedChildren)
                .Where(c => c.ItemId.HasValue)
                .Select(c => c.ItemId!.Value),
        ];

    private bool IsKept(
        BaseItem item,
        IReadOnlyList<Jellyfin.Database.Implementations.Entities.User> users,
        HashSet<Guid> listed
    )
    {
        var episodes =
            item is Series
                ? libraryManager.GetItemIds(
                    new InternalItemsQuery
                    {
                        AncestorIds = [item.Id],
                        Recursive = true,
                        IsDeadPerson = true,
                    }
                )
                : [];
        if (listed.Contains(item.Id) || episodes.Any(listed.Contains))
            return true;

        if (pending.HasBeenUsed(item))
            return true;

        foreach (var user in users)
        {
            if (
                item is Series
                && libraryManager
                    .GetItemIds(
                        new InternalItemsQuery(user)
                        {
                            AncestorIds = [item.Id],
                            Recursive = true,
                            IsPlayed = true,
                            IsDeadPerson = true,
                            Limit = 1,
                        }
                    )
                    .Count > 0
            )
                return true;
        }

        return false;
    }
}
