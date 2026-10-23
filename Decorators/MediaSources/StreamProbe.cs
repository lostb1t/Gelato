using Gelato.RemuxDb;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Subtitles;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators.MediaSources;

/// <summary>
/// Probes a stream row with ffprobe through Jellyfin's probe provider, saves what it found, and
/// downloads the library's subtitle languages for it afterwards.
/// </summary>
internal sealed class StreamProbe(
    IMediaSourceManager inner,
    ILibraryManager libraryManager,
    ILogger<MediaSourceManagerDecorator> log,
    IDirectoryService directoryService,
    Lazy<ISubtitleManager> subtitleManager,
    Lazy<GelatoManager> manager,
    Lazy<IProviderManager> providerManager,
    RemuxDbService remuxDb
)
{
    private readonly IMediaSourceManager _inner = inner;
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly ILogger<MediaSourceManagerDecorator> _log = log;
    private readonly Lazy<GelatoManager> _manager = manager;
    private readonly Lazy<ISubtitleManager> _subtitleManager = subtitleManager;

    // Lazy: ProviderManager depends on ISubtitleManager, which depends on
    // IMediaSourceManager - this decorator.
    private readonly Lazy<IProviderManager> _providerManager = providerManager;

    // Jellyfin builds its metadata providers by type scanning and hands them to
    // IProviderManager; none are registered in the container. So the probe
    // provider has to be looked up there - injecting
    // IEnumerable<ICustomMetadataProvider<Video>> always resolves to an empty list.
    private ICustomMetadataProvider<Video>? FindProbeProvider(Video owner) =>
        _providerManager
            .Value.GetMetadataProviders<Video>(
                owner,
                _libraryManager.GetLibraryOptions(owner),
                includeDisabled: true
            )
            .OfType<ICustomMetadataProvider<Video>>()
            .FirstOrDefault(p => p.Name == "Probe Provider");

    /// <summary>
    /// Saves an item after its probe, as a writer of its movie/episode's rows. Not when it was
    /// deleted while the probe ran: saving it would bring it back.
    /// </summary>
    internal Task SaveProbedAsync(BaseItem probed, Action? beforeSave, CancellationToken ct) =>
        _manager.Value.RunExclusiveAsync(
            (probed as Video)?.PrimaryVersionId ?? probed.Id,
            async token =>
            {
                // Asked of the database as well: an episode deleted with its series stays in the
                // library's cache.
                var current = _libraryManager.GetItemById(probed.Id);
                if (current is null || _libraryManager.RetrieveItem(probed.Id) is null)
                {
                    _log.LogDebug("Not saving the probe of {Id}: it was deleted", probed.Id);
                    return;
                }

                // A sync that ran during the probe saved the item from a copy of its own and made
                // that the library's instance. Saving the probed one as it is would undo the sync:
                // the user it added to the row loses the version until the next sync.
                var saved = probed;
                if (!ReferenceEquals(current, probed))
                {
                    if (probed.IsStreamRow())
                    {
                        TakeSyncedData(current, probed);
                    }
                    else
                    {
                        // A movie/episode gets nothing but its runtime from a probe.
                        current.RunTimeTicks ??= probed.RunTimeTicks;
                        saved = current;
                    }
                }

                beforeSave?.Invoke();
                await saved
                    .UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, token)
                    .ConfigureAwait(false);
            },
            ct
        );

    /// <summary>
    /// Puts what a sync writes on a stream row onto the instance that was probed: its users,
    /// order, names and file (the Gelato data), its URL and its movie/episode. What the probe set
    /// stays.
    /// </summary>
    private static void TakeSyncedData(BaseItem synced, BaseItem probed)
    {
        probed.ExternalId = synced.ExternalId;
        probed.Path = synced.Path;
        probed.ProviderIds = synced.ProviderIds;
        probed.Tags = synced.Tags;
        probed.LockedFields = synced.LockedFields;
        probed.ParentId = synced.ParentId;
        probed.DateLastRefreshed = synced.DateLastRefreshed;
        // No owner is taken too: a split during the probe made the row a legacy one, whose watch
        // state the next sync moves to the movie/episode only while the row has no owner.
        if (synced is Video current && probed is Video row)
            row.SetPrimaryVersionId(current.PrimaryVersionId);
    }

    /// <summary>
    /// How long the probe of a stream not cached on its debrid service answers the requests of
    /// its playback: it is not saved, and Jellyfin's streaming requests, one per segment, would
    /// probe it again. The next playback's PlaybackInfo probes it again.
    /// </summary>
    private static readonly TimeSpan ProbedUncachedTtl = TimeSpan.FromHours(4);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, DateTime> _probedUncached =
        new();

    internal void MarkProbedUncached(Guid rowId) => _probedUncached[rowId] = DateTime.UtcNow;

    internal bool ProbedUncachedRecently(Guid rowId) =>
        _probedUncached.TryGetValue(rowId, out var at) && DateTime.UtcNow - at < ProbedUncachedTtl;

    /// <summary>How long after playback starts a stream with RemuxDB's media info is probed.</summary>
    private static readonly TimeSpan ProbeLaterDelay = TimeSpan.FromSeconds(30);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _probingLater =
        new();

    /// <summary>
    /// Probes a row that played with RemuxDB's media info once, in the background. It adds what
    /// RemuxDB does not record (attachments, such as the fonts of ASS subtitles) and corrects a
    /// wrong match. The segment providers run before playback (<see cref="SegmentRuns.EnsureSegmentsAsync"/>).
    /// Late enough not to hold
    /// up the playback's own requests to the stream.
    /// </summary>
    internal void ProbeLater(Guid rowId)
    {
        if (!_probingLater.TryAdd(rowId, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ProbeLaterDelay).ConfigureAwait(false);

                // A copy from the database: the probe points the item at a temporary file while it
                // runs, and the cached instance serves the playback meanwhile.
                if (
                    _libraryManager.RetrieveItem(rowId) is not Video row
                    || row.GelatoData<string>("mediaInfo") != RemuxDbService.SourceRemuxDb
                    || row.IsUncachedStream()
                    || string.IsNullOrEmpty(row.Path)
                )
                {
                    return;
                }

                var libraryOptions = _libraryManager.GetLibraryOptions(row);
                var before = remuxDb.GetStreams(rowId);
                await ProbeStreamAsync(row, row.Path, CancellationToken.None)
                    .ConfigureAwait(false);

                // As a writer of the movie's rows: a sync meanwhile may have deleted this one, and
                // saving it would bring it back.
                await _manager
                    .Value.RunExclusiveAsync(
                        row.PrimaryVersionId ?? rowId,
                        async ct =>
                        {
                            if (_libraryManager.GetItemById(rowId) is null)
                                return;

                            if (!remuxDb.OnProbed(row, libraryOptions))
                            {
                                // A dead link: keep RemuxDB's, and try again on the next playback.
                                remuxDb.RestoreStreams(rowId, before);
                                return;
                            }

                            remuxDb.CompareWithProbe(rowId, before, remuxDb.GetStreams(rowId));
                            await row.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, ct)
                                .ConfigureAwait(false);
                        },
                        CancellationToken.None
                    )
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Background probe failed for {Id}", rowId);
            }
            finally
            {
                _probingLater.TryRemove(rowId, out _);
            }
        });
    }

    internal async Task ProbeStreamAsync(Video owner, string streamUrl, CancellationToken ct)
    {
        // Named after the row, not the release: Jellyfin's probe takes subtitle files named like
        // the release, which is how Gelato saves them, into the row's streams at index 0 and
        // numbers the file's own after them. A file saved after a PlaybackInfo then moved every
        // index the player had at the next probe. Saved files are listed after the file's
        // streams instead (GetMediaStreamsWithExternalSubs).
        var tmpStrm = Path.Combine(Path.GetTempPath(), $"gelato-probe-{owner.Id:N}.strm");
        await File.WriteAllTextAsync(tmpStrm, streamUrl, ct).ConfigureAwait(false);

        var origPath = owner.Path;
        var origShortcut = owner.IsShortcut;
        owner.Path = tmpStrm;
        owner.IsShortcut = true;
        owner.DateModified = new FileInfo(tmpStrm).LastWriteTimeUtc;

        // The probe's full refresh also downloads the library's subtitle languages, before the
        // playback that waits for the probe starts: a subtitle provider that took 20 to 60 s per
        // file held the click that long. The searches inside the probe find nothing, and the
        // download runs in the background once the probe is done.
        SubtitleManagerDecorator.Probing.Value = true;
        var probed = false;
        try
        {
            _log.LogInformation(
                "Probing stream for {Id} via {Url}",
                owner.Id,
                Redact.Url(streamUrl)
            );

            var options = new MetadataRefreshOptions(directoryService)
            {
                EnableRemoteContentProbe = true,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            };

            var probeProvider = FindProbeProvider(owner);
            if (probeProvider is not null)
            {
                // Call the ffprobe provider directly instead of going through
                // RefreshMetadata.
                //
                // RefreshMetadata runs the whole metadata pipeline, and with
                // FullRefresh that includes ExecuteRemoteProviders - so every
                // stream probe also re-queried OMDb/TMDb for the item. On a
                // library browsed through Gelato that is a remote metadata
                // lookup per probe, and it is where the recurring
                // "Error in The Open Movie Database" JsonException spam comes
                // from: OMDb returns malformed JSON for some season payloads
                // and the probe drags that call along every time.
                //
                // The probe provider on its own does exactly what is wanted
                // here - read the container's streams - and nothing else. The
                // caller already persists the result with
                // UpdateToRepositoryAsync and runs segment providers itself, so
                // no other part of the pipeline is needed. It also keeps image
                // fetchers away from the item while its path points at the
                // temporary .strm file.
                await probeProvider.FetchAsync(owner, options, ct).ConfigureAwait(false);
            }
            else
            {
                // No probe provider resolved - fall back to the old path rather
                // than silently skipping the probe. Logged at information: this
                // path used to be taken on every probe without anyone noticing.
                _log.LogInformation(
                    "No probe provider available for {Id}, falling back to RefreshMetadata",
                    owner.Id
                );
                await owner.RefreshMetadata(options, ct).ConfigureAwait(false);
            }
            probed = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The playback went away or the pre-probe ran out of time: the caller decides what
            // that is, and saving after it would fail on the same token anyway.
            throw;
        }
        catch (ProbedItemDeletedException)
        {
            // Deleted while it was probed: SaveProbedAsync finds it gone and says so.
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Stream probe failed for {Id}", owner.Id);
        }
        finally
        {
            SubtitleManagerDecorator.Probing.Value = false;
            owner.Path = origPath;
            owner.IsShortcut = origShortcut;
            try
            {
                File.Delete(tmpStrm);
            }
            catch
            { /* best effort */
            }
        }

        if (probed)
            DownloadSubtitlesLater(owner.Id);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        Guid,
        byte
    > _downloadingSubtitles = new();

    /// <summary>
    /// Downloads the library's subtitle languages for a probed row, as the probe did inside the
    /// playback before. One run per row at a time: a pre-probe and the click's probe that meet
    /// start one. A subtitle that arrives is listed from its file on the next look at the row.
    /// </summary>
    private void DownloadSubtitlesLater(Guid rowId)
    {
        if (!_downloadingSubtitles.TryAdd(rowId, 0))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                // A copy from the database, with the stream's own path: the cached instance may
                // be probed again meanwhile, which points it at a temporary file.
                if (
                    _libraryManager.RetrieveItem(rowId) is not Video row
                    || row is not (Movie or Episode)
                    || string.IsNullOrEmpty(row.Path)
                )
                {
                    return;
                }

                var options = _libraryManager.GetLibraryOptions(row);
                if (options.SubtitleDownloadLanguages is not { Length: > 0 } languages)
                    return;

                // A dead link probes without a video stream: nothing to find subtitles for.
                var streams = _inner.GetMediaStreams(rowId);
                if (streams.All(s => s.Type != MediaStreamType.Video))
                    return;

                var subtitleManager = _subtitleManager.Value;
                foreach (var language in languages)
                {
                    if (!WantsSubtitle(streams, language, options))
                        continue;

                    var request = new SubtitleSearchRequest
                    {
                        ContentType =
                            row is Episode ? VideoContentType.Episode : VideoContentType.Movie,
                        IndexNumber = row.IndexNumber,
                        Language = language,
                        MediaPath = row.Path,
                        Name = row.Name,
                        ParentIndexNumber = row.ParentIndexNumber,
                        ProductionYear = row.ProductionYear,
                        ProviderIds = row.ProviderIds,
                        SearchAllProviders = false,
                        IsPerfectMatch = options.RequirePerfectSubtitleMatch,
                        DisabledSubtitleFetchers = options.DisabledSubtitleFetchers,
                        SubtitleFetcherOrder = options.SubtitleFetcherOrder,
                        IsAutomated = true,
                    };
                    if (row is Episode episode)
                    {
                        request.IndexNumberEnd = episode.IndexNumberEnd;
                        request.SeriesName = episode.SeriesName;
                    }

                    var found = await subtitleManager
                        .SearchSubtitles(request, CancellationToken.None)
                        .ConfigureAwait(false);
                    if (found.FirstOrDefault() is { } subtitle)
                    {
                        await subtitleManager
                            .DownloadSubtitles(row, subtitle.Id, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Subtitle download after the probe failed for {Id}", rowId);
            }
            finally
            {
                _downloadingSubtitles.TryRemove(rowId, out _);
            }
        });

        // What Jellyfin's SubtitleDownloader skips: a text subtitle in the language, and with the
        // library's options a default audio track or an embedded subtitle in it.
        static bool WantsSubtitle(
            IReadOnlyList<MediaStream> streams,
            string language,
            LibraryOptions options
        )
        {
            bool Is(MediaStream s) =>
                string.Equals(s.Language, language, StringComparison.OrdinalIgnoreCase);

            if (streams.Any(s => s.Type == MediaStreamType.Subtitle && s.IsTextSubtitleStream && Is(s)))
                return false;

            var audio = streams.Where(s => s.Type == MediaStreamType.Audio).ToList();
            var defaultAudio = audio.Where(s => s.IsDefault).ToList();
            if (defaultAudio.Count == 0)
                defaultAudio = audio.Take(1).ToList();
            if (options.SkipSubtitlesIfAudioTrackMatches && defaultAudio.Any(Is))
                return false;

            return !(
                options.SkipSubtitlesIfEmbeddedSubtitlesPresent
                && streams.Any(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal && Is(s))
            );
        }
    }
}
