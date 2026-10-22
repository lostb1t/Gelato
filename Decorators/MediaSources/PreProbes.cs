using Gelato.Services;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators.MediaSources;

/// <summary>
/// Prepares the source a page or version shows ahead of its playback (a pre-probe), at most two at
/// a time, and keeps a playback of the same source from probing it a second time.
/// </summary>
internal sealed class PreProbes(
    MediaSourceManagerDecorator decorator,
    ILibraryManager libraryManager,
    ILogger<MediaSourceManagerDecorator> log,
    IHttpClientFactory httpClientFactory
)
{
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly ILogger<MediaSourceManagerDecorator> _log = log;

    // Sources being pre-probed, by the row they play: a playback of the same source waits for the
    // result instead of probing it a second time. The source's id is not the key: a movie's first
    // stream has the movie's id, whichever row that is at the moment.
    internal static string PreProbeKey(MediaSourceInfo source) => source.ETag ?? source.Id;

    internal IDisposable? ClaimProbe(string key)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _preProbing.TryAdd(key, done.Task) ? new ProbeClaim(this, key, done) : null;
    }

    private sealed class ProbeClaim(
        PreProbes owner,
        string key,
        TaskCompletionSource done
    ) : IDisposable
    {
        public void Dispose()
        {
            owner._preProbing.TryRemove(key, out _);
            done.TrySetResult();
        }
    }

    /// <summary>How long a page or version stays open before it is pre-probed.</summary>
    private static readonly TimeSpan PreProbeDelay = TimeSpan.FromSeconds(1);

    /// <summary>How long a pre-probe may run. A probe takes 2 to 5 seconds.</summary>
    private static readonly TimeSpan PreProbeTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a source whose pre-probe failed is left alone. A dead link would otherwise hold a
    /// slot for <see cref="PreProbeTimeout"/> on every visit. Playback still probes it.
    /// </summary>
    internal static readonly TimeSpan PreProbeRetry = TimeSpan.FromMinutes(30);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        DateTime
    > _preProbeFailed = new();

    private bool PreProbeFailedRecently(string key)
    {
        if (!_preProbeFailed.TryGetValue(key, out var failedAt))
            return false;

        if (DateTime.UtcNow - failedAt < PreProbeRetry)
            return true;

        _preProbeFailed.TryRemove(new KeyValuePair<string, DateTime>(key, failedAt));
        return false;
    }

    /// <summary>
    /// How long a source a playback prepared is not pre-probed. The player loads the item again
    /// when it starts, which schedules a pre-probe of the source it is already playing.
    /// </summary>
    internal static readonly TimeSpan PreparedForPlaybackWindow = TimeSpan.FromMinutes(1);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        DateTime
    > _preparedForPlayback = new();

    internal void MarkPreparedForPlayback(string key)
    {
        var now = DateTime.UtcNow;
        foreach (var (staleKey, preparedAt) in _preparedForPlayback)
        {
            if (now - preparedAt >= PreparedForPlaybackWindow)
                _preparedForPlayback.TryRemove(staleKey, out _);
        }

        _preparedForPlayback[key] = now;
    }

    private bool PreparedForPlaybackRecently(string key) =>
        _preparedForPlayback.TryGetValue(key, out var preparedAt)
        && DateTime.UtcNow - preparedAt < PreparedForPlaybackWindow;

    internal void MarkPreProbeFailed(string key)
    {
        // Sources nobody opens again are dropped once the map grows.
        if (_preProbeFailed.Count > 1000)
        {
            foreach (var (staleKey, failedAt) in _preProbeFailed)
            {
                if (DateTime.UtcNow - failedAt >= PreProbeRetry)
                    _preProbeFailed.TryRemove(staleKey, out _);
            }
        }

        _preProbeFailed[key] = DateTime.UtcNow;
    }

    // Pre-probes running at once. A playback is never held up by this: it probes on its own.
    private readonly SemaphoreSlim _preProbeSlots = new(2);

    // The pre-probe waiting out its delay, per user and title: the next version of the title
    // replaces it, so flicking through the versions probes the one that is stopped on.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        CancellationTokenSource
    > _pendingPreProbe = new();

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _preProbing =
        new();

    /// <summary>The pre-probes under way, by <see cref="PreProbeKey"/>.</summary>
    internal System.Collections.Concurrent.ConcurrentDictionary<string, Task> Running => _preProbing;

    internal void SchedulePreProbe(BaseItem item, MediaSourceInfo source, User user)
    {
        if (
            !GelatoPlugin.Instance!.GetConfig(user.Id).PreProbe
            || item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode)
            || !item.IsGelatoPlaybackItem()
            || IsPlaceholder(source)
            || _preProbing.ContainsKey(PreProbeKey(source))
            || PreProbeFailedRecently(PreProbeKey(source))
        )
        {
            return;
        }

        // Not in the request's context: the request is over when this runs, and the source list it
        // builds must not see it as a page visit again.
        var pending = new CancellationTokenSource();
        var pendingKey = $"{user.Id}:{(item as Video)?.PrimaryVersionId ?? item.Id}";
        _pendingPreProbe.AddOrUpdate(
            pendingKey,
            pending,
            (_, previous) =>
            {
                previous.Cancel();
                return pending;
            }
        );

        using (ExecutionContext.SuppressFlow())
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(PreProbeDelay, pending.Token).ConfigureAwait(false);
                    _pendingPreProbe.TryRemove(
                        new KeyValuePair<string, CancellationTokenSource>(pendingKey, pending)
                    );
                    await PreProbeAsync(item, source, user).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Another page or version took its place.
                }
                finally
                {
                    pending.Dispose();
                }
            });
        }
    }

    /// <summary>
    /// Prepares the source for playback as a playback request would: probes it when it needs one
    /// and looks its segments up. Does nothing for a source already being prepared.
    /// </summary>
    private async Task PreProbeAsync(BaseItem item, MediaSourceInfo source, User user)
    {
        var key = PreProbeKey(source);
        if (PreProbeFailedRecently(key) || PreparedForPlaybackRecently(key))
            return;

        // Registered only once running: a playback waits for a pre-probe under way, not for one
        // queued behind the others.
        await _preProbeSlots.WaitAsync().ConfigureAwait(false);

        // Switched off while this waited for its delay or a slot.
        if (!GelatoPlugin.Instance!.GetConfig(user.Id).PreProbe)
        {
            _preProbeSlots.Release();
            _log.LogDebug(
                "Pre-probe of {Id} source {Source} dropped: pre-probing is off",
                item.Id,
                source.Id
            );
            return;
        }

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_preProbing.TryAdd(key, done.Task))
        {
            _preProbeSlots.Release();
            return;
        }

        // A stream that takes the connection and never answers would hold its slot, and the
        // playbacks waiting for it, for good.
        using var timeout = new CancellationTokenSource(PreProbeTimeout);
        try
        {
            // Deleted while this waited: the instance at hand would be probed and saved back. An
            // item inserted again since has the same id, so the library's instance is the one.
            if (_libraryManager.GetItemById(item.Id) is not { } current)
                return;

            // A source with its media info is not probed, so its link is checked here: a dead one
            // is passed over by the playback that follows (FailOver). Unprobed ones are checked by
            // their probe.
            if (
                GelatoPlugin.Instance!.Configuration.PlaybackFailover
                && !PlaybackPreparation.NeedsProbe(source)
                && StreamLinks.AsHttpUri(source.Path) is { } uri
            )
            {
                var reason = await StreamLinks
                    .CheckAsync(httpClientFactory.CreateClient(), uri, StreamFailover.StartTimeout, timeout.Token)
                    .ConfigureAwait(false);
                if (reason is not null)
                {
                    decorator.Failover.MarkDead(key);
                    // No URL in the log: stream links carry the debrid key.
                    _log.LogWarning(
                        "Pre-probe of {Id} source {Source}: the stream did not start ({Reason})",
                        item.Id,
                        source.Id,
                        reason
                    );
                    return;
                }
            }

            await decorator.Playback.GetPlaybackMediaSourcesCore(
                    current,
                    user,
                    true,
                    false,
                    source.Id,
                    true,
                    timeout.Token
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            MarkPreProbeFailed(key);
            _log.LogWarning(
                "Pre-probe of {Id} source {Source} gave up after {Seconds}s",
                item.Id,
                source.Id,
                PreProbeTimeout.TotalSeconds
            );
        }
        catch (Exception ex)
        {
            MarkPreProbeFailed(key);
            _log.LogWarning(ex, "Pre-probe failed for {Id} source {Source}", item.Id, source.Id);
        }
        finally
        {
            _preProbing.TryRemove(key, out _);
            done.TrySetResult();
            _preProbeSlots.Release();
        }
    }

    /// <summary>
    /// Pre-probes the source an episode plays by default, syncing its streams first when they
    /// are not: the next episode of one that is nearing its end.
    /// </summary>
    public async Task PreProbeDefaultAsync(BaseItem item, User user)
    {
        if (
            !GelatoPlugin.Instance!.GetConfig(user.Id).PreProbe
            || item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode)
            || !item.IsGelatoPlaybackItem()
        )
        {
            return;
        }

        await decorator.Sync.SyncIfDueAsync(item, user.Id).ConfigureAwait(false);

        var sources = decorator.GetStaticMediaSources(item, false, user);
        if (sources.Count > 0 && !IsPlaceholder(sources[0]))
            await PreProbeAsync(item, sources[0], user).ConfigureAwait(false);
    }

    /// <summary>
    /// The movie/episode's own placeholder, listed when the user has no streams: there is nothing
    /// to probe, and probing it would save the movie on every visit. Asked before the source
    /// goes into a response, which stubs its path.
    /// </summary>
    internal static bool IsPlaceholder(MediaSourceInfo source) =>
        string.IsNullOrEmpty(source.Path) || source.Path.IsPlaceholderPath();
}
