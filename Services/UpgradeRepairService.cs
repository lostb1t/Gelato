using Gelato.ScheduledTasks;
using MediaBrowser.Controller;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>
/// Notices when the server has been upgraded to Jellyfin 12 underneath us and runs the watch state
/// repair once, on the boot where it is needed.
/// </summary>
/// <remarks>
/// The first Jellyfin 12 start deletes every Gelato item, because the migration that prunes stale
/// entries treats a URL-backed path as an item that belongs to no library. The items come back as
/// catalogs re-import them, but the watch state does not follow on its own, and what nothing
/// re-imports never comes back at all.
///
/// <see cref="RepairWatchStateTask"/> fixes that, and deliberately has no schedule: a parked user
/// data row records when its item was removed but never why, so the task cannot tell a library the
/// upgrade destroyed from one somebody cleared out on purpose. Deciding that is not the task's job.
/// It is this service's, and it decides on a fact rather than a guess: the major version changed
/// between the last start and this one. That happens exactly once, and only to an install that was
/// already here.
///
/// The first start after installing a Gelato that keeps track has no previous version to compare
/// against, and is treated the same way. Waiting for a change that may already have happened would
/// miss precisely the people this exists for — the ones who upgraded Jellyfin without ever running
/// the release that would have left a record. On an install that lost nothing the repair finds
/// nothing to re-import; rows parked earlier for items that are still here are looked at and left
/// alone, so running it costs a few queries and changes nothing.
/// </remarks>
public sealed class UpgradeRepairService(
    IServerApplicationHost appHost,
    ITaskManager taskManager,
    ILogger<UpgradeRepairService> log
) : IHostedService
{
    private const int JellyfinMajorThatDeletesGelatoItems = 12;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (Check())
            {
                // Scheduled tasks are not registered until after the hosted services have started,
                // so queueing here would be too early — the task manager does not know the task yet.
                // Wait for it in the background rather than holding up startup.
                _ = QueueWhenRegisteredAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // Never hold up startup. The task can always be run by hand.
            log.LogWarning(ex, "Could not check whether Jellyfin was upgraded");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task QueueWhenRegisteredAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);

        try
        {
            while (DateTime.UtcNow < deadline)
            {
                if (taskManager.ScheduledTasks.Any(t => t.ScheduledTask is RepairWatchStateTask))
                {
                    taskManager.QueueScheduledTask<RepairWatchStateTask>();
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }

            log.LogWarning(
                "Gelato: the watch state repair never registered, so it was not started. Run it by hand from Scheduled Tasks."
            );
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Could not start the watch state repair");
        }
    }

    private bool Check()
    {
        var plugin = GelatoPlugin.Instance;
        if (plugin is null)
        {
            return false;
        }

        var current = appHost.ApplicationVersion;
        var recorded = plugin.Configuration.LastSeenServerVersion;

        if (!Version.TryParse(recorded, out var previous))
        {
            // Nothing recorded, so this is the first start of a Gelato that keeps track. The install
            // it is starting on may well have been upgraded already, and waiting for a version
            // change that has been and gone would help nobody: the people who need this most are
            // exactly the ones who never ran the release that would have left a record.
            //
            // Running it regardless is safe. On an install that lost nothing there is nothing to
            // re-import, and whatever was parked earlier for items still in the library is left
            // where it is.
            Remember(plugin, current);

            if (current.Major >= JellyfinMajorThatDeletesGelatoItems)
            {
                log.LogInformation(
                    "Gelato: first start keeping track of the Jellyfin version, on {Version}. Running the watch state repair once in case this install was upgraded before Gelato could notice.",
                    current
                );

                return true;
            }

            return false;
        }

        if (previous == current)
        {
            return false;
        }

        Remember(plugin, current);

        if (
            previous.Major < JellyfinMajorThatDeletesGelatoItems
            && current.Major >= JellyfinMajorThatDeletesGelatoItems
        )
        {
            log.LogWarning(
                "Gelato: Jellyfin was upgraded from {Previous} to {Current}, which removes Gelato items and detaches their watch state. Running the watch state repair.",
                previous,
                current
            );

            return true;
        }

        log.LogInformation(
            "Gelato: Jellyfin changed from {Previous} to {Current}",
            previous,
            current
        );
        return false;
    }

    private static void Remember(GelatoPlugin plugin, Version version)
    {
        plugin.Configuration.LastSeenServerVersion = version.ToString();

        // SaveConfiguration rather than UpdateConfiguration: recording this must not drop caches or
        // raise the configuration-changed event.
        plugin.SaveConfiguration();
    }
}
