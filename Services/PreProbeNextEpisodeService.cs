using System.Collections.Concurrent;
using Gelato.Decorators;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>
/// Pre-probes the next episode while an episode nears its end, so autoplay does not wait for the
/// probe (<see cref="PluginConfiguration.PreProbe"/>).
/// </summary>
public sealed class PreProbeNextEpisodeService(
    ISessionManager sessionManager,
    IMediaSourceManager mediaSourceManager,
    ILibraryManager libraryManager,
    ILogger<PreProbeNextEpisodeService> log
) : IHostedService
{
    /// <summary>The share of an episode played from which the next one is prepared.</summary>
    private const double Threshold = 0.85;

    // (user, episode) pairs already handled: progress is reported every few seconds. Starting the
    // episode again arms it again.
    private readonly ConcurrentDictionary<(Guid User, Guid Episode), byte> _handled = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        sessionManager.PlaybackStart += OnStart;
        sessionManager.PlaybackProgress += OnProgress;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        sessionManager.PlaybackStart -= OnStart;
        sessionManager.PlaybackProgress -= OnProgress;
        return Task.CompletedTask;
    }

    private void OnStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (e.Users is not { Count: > 0 } users || GetEpisode(e) is not { } episode)
            return;

        foreach (var user in users)
            _handled.TryRemove((user.Id, episode.Id), out _);
    }

    /// <summary>The episode being played: the item itself or the owner of the stream row played.</summary>
    private Episode? GetEpisode(PlaybackProgressEventArgs e)
    {
        var played =
            e.Item is Video { PrimaryVersionId: { } primaryId }
                ? libraryManager.GetItemById(primaryId)
                : e.Item;
        return played as Episode;
    }

    private void OnProgress(object? sender, PlaybackProgressEventArgs e)
    {
        if (
            mediaSourceManager is not MediaSourceManagerDecorator decorator
            || e.PlaybackPositionTicks is not > 0
            || e.Users is not { Count: > 0 } users
        )
        {
            return;
        }

        var episode = GetEpisode(e);
        var runtime = e.Item.RunTimeTicks ?? episode?.RunTimeTicks ?? 0;
        if (
            episode is null
            || !episode.IsGelatoPlaybackItem()
            || runtime <= 0
            || e.PlaybackPositionTicks < runtime * Threshold
        )
        {
            return;
        }

        foreach (var user in users)
        {
            if (!_handled.TryAdd((user.Id, episode.Id), 0))
                continue;

            if (_handled.Count > 1000)
                _handled.Clear();

            _ = Task.Run(async () =>
            {
                try
                {
                    if (FindNext(episode) is not { } next)
                        return;

                    log.LogDebug(
                        "Pre-probing {Next} while {Episode} is nearing its end",
                        next.Id,
                        episode.Id
                    );
                    await decorator.PreProbeDefaultAsync(next, user).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Pre-probe of the episode after {Id} failed", episode.Id);
                }
            });
        }
    }

    private Episode? FindNext(Episode episode)
    {
        if (episode.SeriesId == Guid.Empty)
            return null;

        return libraryManager
            .GetItemList(
                new InternalItemsQuery
                {
                    AncestorIds = [episode.SeriesId],
                    IncludeItemTypes = [BaseItemKind.Episode],
                    Recursive = true,
                    OrderBy =
                    [
                        (ItemSortBy.ParentIndexNumber, SortOrder.Ascending),
                        (ItemSortBy.IndexNumber, SortOrder.Ascending),
                    ],
                }
            )
            .OfType<Episode>()
            .SkipWhile(e => e.Id != episode.Id)
            .Skip(1)
            .FirstOrDefault();
    }
}
