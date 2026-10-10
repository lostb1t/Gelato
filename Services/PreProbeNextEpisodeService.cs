using System.Collections.Concurrent;
using Gelato.Decorators;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
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
/// Prepares the next episode while an episode plays. It follows the binge group of the stream
/// that plays, so a series keeps its quality and release from one episode to the next, and is
/// pre-probed while the episode nears its end, so autoplay does not wait for the probe
/// (<see cref="PluginConfiguration.PreProbe"/>).
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

        // From the start, not only near the end: skipping to the next episode follows the group
        // too.
        if (
            mediaSourceManager is MediaSourceManagerDecorator decorator
            && episode.IsGelatoPlaybackItem()
        )
        {
            PrepareNext(decorator, e, episode, users, false);
        }
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

        var due = users.Where(user => _handled.TryAdd((user.Id, episode.Id), 0)).ToList();
        if (_handled.Count > 1000)
            _handled.Clear();

        if (due.Count > 0)
            PrepareNext(decorator, e, episode, due, true);
    }

    /// <summary>
    /// Makes the next episode follow the binge group of the stream that plays, then pre-probes
    /// the stream it plays by default: with the group set first, that is the group's.
    /// </summary>
    private void PrepareNext(
        MediaSourceManagerDecorator decorator,
        PlaybackProgressEventArgs e,
        Episode episode,
        IReadOnlyList<User> users,
        bool preProbe
    )
    {
        var played = e.Item;
        var mediaSourceId = e.MediaSourceId;
        _ = Task.Run(async () =>
        {
            try
            {
                if (FindNext(episode) is not { } next)
                    return;

                foreach (var user in users)
                {
                    // Not while the user plays the next episode elsewhere: its stream would
                    // change under that playback.
                    if (
                        !sessionManager.Sessions.Any(s =>
                            s.UserId == user.Id && s.NowPlayingItem?.Id == next.Id
                        )
                    )
                    {
                        decorator.FollowBingeGroup(played, mediaSourceId, next, user);
                    }

                    if (!preProbe)
                        continue;

                    log.LogDebug(
                        "Pre-probing {Next} while {Episode} is nearing its end",
                        next.Id,
                        episode.Id
                    );
                    await decorator.PreProbeDefaultAsync(next, user).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Preparing the episode after {Id} failed", episode.Id);
            }
        });
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
