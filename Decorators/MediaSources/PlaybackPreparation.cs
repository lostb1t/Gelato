using System.Globalization;
using Gelato.RemuxDb;
using Gelato.Services;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators.MediaSources;

/// <summary>
/// Prepares the source a playback asks for: picks it (SyncPlay's group, failover), probes it when
/// it needs one, looks its segments up, and stubs its path in a PlaybackInfo answer.
/// </summary>
internal sealed class PlaybackPreparation(
    MediaSourceManagerDecorator decorator,
    IMediaSourceManager inner,
    ILibraryManager libraryManager,
    ILogger<MediaSourceManagerDecorator> log,
    IHttpContextAccessor http,
    Lazy<GelatoManager> manager,
    RemuxDbService remuxDb,
    SegmentLookups segmentLookups,
    SyncPlaySources syncPlaySources
)
{
    private readonly IMediaSourceManager _inner = inner;
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly ILogger<MediaSourceManagerDecorator> _log = log;
    private readonly IHttpContextAccessor _http = http;
    private readonly Lazy<GelatoManager> _manager = manager;

    /// <summary>
    /// How long a source prepared for streaming answers the requests that follow it, counted
    /// from the end of its preparation.
    /// </summary>
    private static readonly TimeSpan PreparedTtl = TimeSpan.FromSeconds(10);

    // Done completes with the time the preparation ended, however it ended.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        (Task<IReadOnlyList<MediaSourceInfo>> Sources, Task<DateTime> Done)
    > _prepared = new();

    private static bool PreparedExpired(Task<DateTime> done, DateTime now) =>
        done.IsCompletedSuccessfully && done.Result + PreparedTtl <= now;

    /// <remarks>
    /// Starting a playback asks for its source several times within a second: PlaybackInfo, then
    /// Jellyfin's HLS controller for the master playlist, the variant playlist and the first
    /// segment (the pre-probe the player's reload of the item schedules stays out, see
    /// <see cref="PreProbes.PreparedForPlaybackWindow"/>). Each preparation reads the item's versions and
    /// streams and may look up segments, so the streaming requests share the first one's answer
    /// while it is prepared and for <see cref="PreparedTtl"/> after that: the first probe of a
    /// remote stream can take up to a minute, and the requests that follow come only once it is
    /// over. PlaybackInfo is prepared on its own: its answer is stubbed.
    /// </remarks>
    public async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSources(
        BaseItem item,
        User user,
        bool allowMediaProbe,
        bool enablePathSubstitution,
        CancellationToken ct
    )
    {
        if (
            item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode)
            || _http.ReadRequest(ctx => ctx.IsPlaybackInfoAction(), false)
        )
        {
            return await GetPlaybackMediaSourcesCore(
                    item,
                    user,
                    allowMediaProbe,
                    enablePathSubstitution,
                    null,
                    false,
                    ct
                )
                .ConfigureAwait(false);
        }

        var sourceId = _http.ReadRequest(
            ctx => ctx.Items.TryGetValue("MediaSourceId", out var idObj) ? idObj as string : null,
            null
        );
        var key = string.Join(
            ':',
            item.Id.ToString("N", CultureInfo.InvariantCulture),
            UserKey(user),
            sourceId?.ToLowerInvariant(),
            allowMediaProbe,
            enablePathSubstitution
        );

        var now = DateTime.UtcNow;
        if (_prepared.TryGetValue(key, out var prepared) && !PreparedExpired(prepared.Done, now))
        {
            try
            {
                var sources = await prepared.Sources.WaitAsync(ct).ConfigureAwait(false);
                var done = await prepared.Done.ConfigureAwait(false);

                // 0 for a request that waited for the preparation to end.
                _log.LogDebug(
                    "GetPlaybackMediaSources {ItemId} mediaSourceId={MediaSourceId}: prepared {Ago} ms ago",
                    item.Id,
                    sourceId,
                    (int)Math.Max(0, (now - done).TotalMilliseconds)
                );
                return sources;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // The request that prepared it failed or was cancelled: this one prepares it again.
            }
        }

        foreach (var (k, entry) in _prepared)
        {
            if (PreparedExpired(entry.Done, now))
                _prepared.TryRemove(k, out _);
        }

        var task = GetPlaybackMediaSourcesCore(
            item,
            user,
            allowMediaProbe,
            enablePathSubstitution,
            null,
            false,
            ct
        );
        _prepared[key] = (
            task,
            task.ContinueWith(
                _ => DateTime.UtcNow,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            )
        );
        return await task.ConfigureAwait(false);
    }

    // Jellyfin's streaming requests pass no user, the declared type notwithstanding.
    private static string? UserKey(User? user) =>
        user?.Id.ToString("N", CultureInfo.InvariantCulture);

    /// <param name="requestedSourceId">The source to prepare; null reads it from the request.</param>
    /// <param name="preProbe">
    /// Prepares the source ahead of playback: nothing is stubbed, and a row on RemuxDB's media
    /// info gets its segment lookup but not the delayed probe, which would resolve a stream
    /// nobody may play.
    /// </param>
    internal async Task<IReadOnlyList<MediaSourceInfo>> GetPlaybackMediaSourcesCore(
        BaseItem item,
        User user,
        bool allowMediaProbe,
        bool enablePathSubstitution,
        string? requestedSourceId,
        bool preProbe,
        CancellationToken ct,
        string? preProbeKey = null
    )
    {
        if (item.GetBaseItemKind() is not (BaseItemKind.Movie or BaseItemKind.Episode))
        {
            return await _inner
                .GetPlaybackMediaSources(item, user, allowMediaProbe, enablePathSubstitution, ct)
                .ConfigureAwait(false);
        }

        var manager = _manager.Value;
        var sources = decorator.GetStaticMediaSources(item, enablePathSubstitution, user);

        requestedSourceId ??= _http.ReadRequest(
            ctx => ctx.Items.TryGetValue("MediaSourceId", out var idObj) ? idObj as string : null,
            null
        );
        var named = requestedSourceId is not null;
        Guid? mediaSourceId =
            Guid.TryParse(requestedSourceId, out var fromCtx)
                ? fromCtx
                : (
                    item.IsPrimaryVersion()
                    && sources.Count > 0
                    && Guid.TryParse(sources[0].Id, out var fromSource)
                        ? fromSource
                        : null
                );

        _log.LogDebug(
            "GetPlaybackMediaSources {ItemId} mediaSourceId={MediaSourceId} action={Action} preProbe={PreProbe}",
            item.Id,
            mediaSourceId,
            _http.ReadRequest(ctx => ctx.GetActionName(), null),
            preProbe
        );

        // SyncPlay starts its members on the item without a version: they get the group's.
        var syncPlayGroup =
            !preProbe && item.IsGelatoPlaybackItem() && !item.IsStreamRow()
                ? _http.ReadRequest(ctx => syncPlaySources.GroupOf(ctx), null)
                : null;
        var picked =
            Guid.TryParse(requestedSourceId, out var requestedId) && requestedId != item.Id;
        if (
            syncPlayGroup is not null
            && !picked
            && _http.ReadRequest(ctx => syncPlaySources.SourceFor(ctx, item.Id), null)
                is { } groupSource
            && Guid.TryParse(groupSource, out var groupSourceId)
        )
        {
            _log.LogDebug(
                "GetPlaybackMediaSources {ItemId}: SyncPlay group plays {SourceId}",
                item.Id,
                groupSourceId
            );
            mediaSourceId = groupSourceId;
        }

        var selected = SelectByIdOrFirst(sources, mediaSourceId);
        if (selected is null)
            return sources;

        // A pre-probe is for the row it was scheduled for. A sync since may have made another row
        // the default (the first source carries the movie's id): the visit that synced pre-probes
        // that one, under its own key, and probing it here too probed it twice at once.
        if (preProbeKey is not null && PreProbes.PreProbeKey(selected) != preProbeKey)
        {
            _log.LogDebug(
                "Pre-probe of {ItemId} dropped: its default stream is another row now",
                item.Id
            );
            return sources;
        }

        var canMoveOn = false;
        if (!preProbe)
        {
            selected = _http.ReadRequest(ctx => ctx.IsPlaybackInfoAction(), false)
                ? decorator.Failover.FailOver(item, sources, selected, out canMoveOn)
                : decorator.Failover.FailedOver(sources, selected);
        }

        // A version picked by a member is the group's from now on.
        if (
            syncPlayGroup is { } group
            && picked
            && Guid.TryParse(selected.Id, out var selectedId)
            && selectedId != item.Id
        )
        {
            syncPlaySources.Choose(group, item.Id, selected.Id);
        }

        var owner = ResolveOwnerFor(selected, item);
        if (!owner.IsGelatoPlaybackItem())
        {
            // A local movie's linked stream rows are in Jellyfin's list too, with their real URLs.
            // They are played through their own source id, which the branch below handles. A file
            // merged into a Gelato movie is asked for itself: Jellyfin would probe the movie's
            // placeholder path otherwise.
            var streamRowIds = VersionList.GetStreamRowIds(decorator.Versions.GetStreamRows(item as Video));
            var playbackSources = await _inner
                .GetPlaybackMediaSources(
                    item.IsGelatoPlaybackItem() ? owner : item,
                    user,
                    allowMediaProbe,
                    enablePathSubstitution,
                    ct
                )
                .ConfigureAwait(false);
            return playbackSources.Where(s => !streamRowIds.Contains(s.Id)).ToList();
        }

        if (owner.IsPrimaryVersion() && owner.Id != item.Id)
        {
            sources = decorator.GetStaticMediaSources(owner, enablePathSubstitution, user);
            selected = Select(sources);
            if (selected is null)
                return sources;
        }

        // Ahead of playback only the source that was asked for is prepared. When the title's rows
        // changed since the page was opened (a split, a sync), the list falls back to another
        // source or to the title's placeholder, and probing that would save the movie/episode
        // in the middle of the change.
        if (
            preProbe
            && (
                PreProbes.IsPlaceholder(selected)
                || !string.Equals(selected.Id, requestedSourceId, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return sources;
        }

        // A row probed before is done ahead of playback: one whose probe found a file too short
        // still looks unprobed, and would be probed again on every visit. Not a row that lost
        // the tracks its probe found: playback would probe that one at the click.
        if (
            preProbe
            && owner.GelatoData<string>("mediaInfo") == RemuxDbService.SourceProbe
            && HasVideo(selected)
        )
        {
            await decorator.Segments.EnsureSegmentsAsync(owner, ct).ConfigureAwait(false);
            return sources;
        }

        // A pre-probe of this source is under way: its result is what plays, not a second probe.
        if (!preProbe && decorator.PreProbes.Running.TryGetValue(PreProbes.PreProbeKey(selected), out var running))
        {
            await running.WaitAsync(ct).ConfigureAwait(false);
            sources = decorator.GetStaticMediaSources(
                owner.IsPrimaryVersion() ? owner : item,
                enablePathSubstitution,
                user
            );
            selected = Select(sources) ?? selected;
        }

        // RemuxDB's media info plays as it is and is probed afterwards, unless it lacks what the
        // playback decides on.
        // A title without streams plays its placeholder, whose gelato:// path Jellyfin's probe
        // refuses: probing it only cost the click, twice.
        var fromRemuxDb = owner.GelatoData<string>("mediaInfo") == RemuxDbService.SourceRemuxDb;
        // A stream not cached on its debrid service is probed at each playback's PlaybackInfo,
        // and the requests of that playback go by that probe: it is not saved.
        var uncached = !preProbe && owner.IsUncachedStream();
        var needsProbe =
            !PreProbes.IsPlaceholder(selected)
            && (
                NeedsProbe(selected, owner)
                || (fromRemuxDb && RemuxDbMapper.LacksPlaybackInfo(selected.MediaStreams))
            )
            && !(
                uncached
                && !_http.ReadRequest(ctx => ctx.IsPlaybackInfoAction(), false)
                && decorator.Probe.ProbedUncachedRecently(owner.Id)
            );

        // A pre-probe that starts while this playback probes sees it and stays out.
        var probeKey = PreProbes.PreProbeKey(selected);
        using var claim = needsProbe && !preProbe ? decorator.PreProbes.ClaimProbe(probeKey) : null;
        if (needsProbe)
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(owner);
            var remuxDbStreams = fromRemuxDb ? remuxDb.GetStreams(owner.Id) : null;

            var segmentTask = segmentLookups.IsDue(owner, libraryOptions)
                ? decorator.Segments.RunSegmentProvidersAsync(owner, libraryOptions, ct)
                : Task.CompletedTask;
            var metadataTask = decorator.Probe.ProbeStreamAsync((Video)owner, selected.Path, ct);
            //  var subtitleTask = DownloadSubtitles((Video)owner, ct);

            await Task.WhenAll(metadataTask, segmentTask).ConfigureAwait(false);

            // The debrid service's clip: its runtime and tracks are not saved on the row, so the
            // row is neither listed as a clip nor kept with the clip's details once the file is
            // there. The tracks Jellyfin's probe wrote serve this playback; the next sync drops
            // them (RemuxDbService.ApplyCached).
            if (uncached)
            {
                decorator.Probe.MarkProbedUncached(owner.Id);
            }
            else
            {
                await decorator.Probe.SaveProbedAsync(
                    owner,
                    () =>
                    {
                        if (
                            owner is Video probedRow
                            && probedRow.IsStreamRow()
                            && !remuxDb.OnProbed(probedRow, libraryOptions)
                            && remuxDbStreams is not null
                        )
                        {
                            // A dead link: keep RemuxDB's, and try again on the next playback.
                            remuxDb.RestoreStreams(owner.Id, remuxDbStreams);
                        }
                    },
                    ct
                )
                .ConfigureAwait(false);
            }

            // The probe saved the file's chapters, which the lookup before it could not see.
            if (SegmentLookups.UsesChapters(owner))
                await decorator.Segments.RunSegmentsAsync(owner, ct).ConfigureAwait(false);

            var refreshed = decorator.GetStaticMediaSources(item, enablePathSubstitution, user);
            selected = Select(refreshed);
            sources = refreshed;

            // The source just probed, wherever it is listed now. A debrid's clip goes after the
            // others (IsShortClip), and the stream that takes its place under the id asked for is
            // not the one probed: judged by that one, a working clip looked dead.
            var probed = refreshed.FirstOrDefault(s => PreProbes.PreProbeKey(s) == probeKey);

            // Still unprobed after its probe: a dead link, left alone for a while.
            if (preProbe && (probed is null || NeedsProbe(probed, owner)))
                decorator.PreProbes.MarkPreProbeFailed(probeKey);

            // No video at all: a dead stream, which a playback that may moves on from. One that
            // is only short (a clip) plays.
            if ((preProbe || canMoveOn) && (probed is null || !HasVideo(probed)))
            {
                decorator.Failover.MarkDead(probeKey);
                if (canMoveOn)
                {
                    // No URL in the log: stream links carry the debrid key.
                    _log.LogWarning(
                        "A stream of {Item} did not start: its probe found no video",
                        item.Name
                    );
                    return await GetPlaybackMediaSourcesCore(
                            item,
                            user,
                            allowMediaProbe,
                            enablePathSubstitution,
                            requestedSourceId,
                            false,
                            ct
                        )
                        .ConfigureAwait(false);
                }
            }

            // The probe found a clip, listed after the others now: the playback goes on with the
            // stream in its place, prepared as its own playback would be. A clip picked by its
            // own id is still the one asked for, and plays.
            if (
                !preProbe
                && probed is not null
                && selected is not null
                && PreProbes.PreProbeKey(selected) != probeKey
                && NeedsProbe(selected, ResolveOwnerFor(selected, item))
            )
            {
                return await GetPlaybackMediaSourcesCore(
                        item,
                        user,
                        allowMediaProbe,
                        enablePathSubstitution,
                        requestedSourceId,
                        false,
                        ct
                    )
                    .ConfigureAwait(false);
            }

            if (selected is null)
                return refreshed;
        }
        else
        {
            // Also a row probed before: one IntroDB had nothing for is looked up again after a
            // while, and one whose chapters were never read gets their segments.
            await decorator.Segments.EnsureSegmentsAsync(owner, ct).ConfigureAwait(false);
            if (fromRemuxDb && !preProbe)
                decorator.Probe.ProbeLater(owner.Id);
        }

        if (!uncached && item.RunTimeTicks is null && selected.RunTimeTicks is not null)
        {
            item.RunTimeTicks = selected.RunTimeTicks;
            await decorator.Probe.SaveProbedAsync(item, null, ct).ConfigureAwait(false);
        }

        if (!preProbe)
            decorator.PreProbes.MarkPreparedForPlayback(PreProbes.PreProbeKey(selected));

        // Stub path after probing is done so the real URL is never sent to clients.
        // Force File protocol so clients proxy through Jellyfin instead of direct-playing.
        // Both playback info actions, not the POST alone: native clients use the GET.
        var playbackInfo =
            !preProbe && _http.ReadRequest(ctx => ctx.IsPlaybackInfoAction(), false);
        if (playbackInfo)
        {
            selected.Stub();
        }

        // A client that named no version picks among the ones the answer offers: JellyCon's and
        // Kodi's dialog list them. All of them then, the default first and the only one prepared;
        // another is probed when it is played. Only while the default is the item's own source:
        // Web and Android TV play the source with the item's id when there is one, so a resumed
        // version, the binge group's stream or the SyncPlay group's would lose to it.
        if (
            playbackInfo
            && !named
            && !PreProbes.IsPlaceholder(selected)
            && Guid.TryParse(selected.Id, out var defaultId)
            && defaultId == item.Id
        )
        {
            var others = sources
                .Where(s =>
                    !ReferenceEquals(s, selected)
                    && s.Id != selected.Id
                    && PreProbes.PreProbeKey(s) != PreProbes.PreProbeKey(selected)
                    && !PreProbes.IsPlaceholder(s)
                )
                .ToList();
            foreach (var other in others)
                other.Stub();
            return [selected, .. others];
        }

        return [selected];

        // The source asked for, or the stream a playback of it moved on to.
        MediaSourceInfo? Select(IReadOnlyList<MediaSourceInfo> list)
        {
            var s = SelectByIdOrFirst(list, mediaSourceId);
            return s is null || preProbe ? s : decorator.Failover.FailedOver(list, s);
        }

        // Gelato's sources name their item in the ETag (the first stream carries the movie's id).
        // Jellyfin's own, like a file merged in as a version, use the item's id.
        BaseItem ResolveOwnerFor(MediaSourceInfo s, BaseItem fallback) =>
            (Guid.TryParse(s.ETag, out var etag) ? _libraryManager.GetItemById(etag) : null)
            ?? (Guid.TryParse(s.Id, out var id) ? _libraryManager.GetItemById(id) : null)
            ?? fallback;
    }

    internal static bool HasVideo(MediaSourceInfo s) =>
        s.MediaStreams?.Any(ms => ms.Type == MediaStreamType.Video) ?? false;

    internal static bool NeedsProbe(MediaSourceInfo s) =>
        !HasVideo(s) || (s.RunTimeTicks ?? 0) < TimeSpan.FromMinutes(2).Ticks;

    /// <summary>
    /// <see cref="NeedsProbe(MediaSourceInfo)"/>, except for a short row its probe found to be
    /// short: a trailer the addon listed first, or a debrid's clip for a file it does not have
    /// yet. Probing it again found the same clip, 0.7 s at PlaybackInfo and again at the master
    /// playlist of every play. The debrid's clip stays one at later plays and is listed last
    /// (<see cref="VersionOrder.IsShortClip"/>).
    /// </summary>
    internal static bool NeedsProbe(MediaSourceInfo s, BaseItem owner) =>
        !HasVideo(s)
        || (
            (s.RunTimeTicks ?? 0) < TimeSpan.FromMinutes(2).Ticks
            && owner.GelatoData<string>("mediaInfo") != RemuxDbService.SourceProbe
        );

    /// <summary>
    /// The source with the id, or the first one. A movie/episode's first stream is listed with
    /// the movie's id and names its row in the ETag only, so a row id a client kept from an
    /// earlier list is looked for there too. That source then answers under the row's id:
    /// Jellyfin picks the source to play out of the answer by the id that was asked for.
    /// </summary>
    private static MediaSourceInfo? SelectByIdOrFirst(IReadOnlyList<MediaSourceInfo> list, Guid? id)
    {
        if (!id.HasValue)
            return list.FirstOrDefault();

        var target = id.Value;

        var byId = list.FirstOrDefault(s =>
            !string.IsNullOrEmpty(s.Id) && Guid.TryParse(s.Id, out var g) && g == target
        );
        if (byId is not null)
            return byId;

        var byRow = list.FirstOrDefault(s => Guid.TryParse(s.ETag, out var g) && g == target);
        if (byRow is not null)
            byRow.Id = byRow.ETag;

        return byRow ?? list.FirstOrDefault();
    }
}
