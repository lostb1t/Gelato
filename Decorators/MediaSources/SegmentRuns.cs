using Gelato.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators.MediaSources;

/// <summary>
/// Runs the media segment providers (intro and credits) for a row before its playback, once at
/// a time per row.
/// </summary>
internal sealed class SegmentRuns(
    ILibraryManager libraryManager,
    ILogger<MediaSourceManagerDecorator> log,
    IMediaSegmentManager mediaSegmentManager,
    SegmentLookups segmentLookups
)
{
    private readonly ILibraryManager _libraryManager = libraryManager;
    private readonly ILogger<MediaSourceManagerDecorator> _log = log;
    private readonly IMediaSegmentManager _mediaSegmentManager = mediaSegmentManager;

    /// <summary>How long playback waits for the segment providers of a row that plays on RemuxDB's media info.</summary>
    private static readonly TimeSpan SegmentWait = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Fetches the row's segments before playback starts, so the client has them for its skip
    /// buttons on the first play, unless the provider has an answer for the row that still holds
    /// (<see cref="SegmentLookups.IsDue"/>).
    /// </summary>
    internal Task EnsureSegmentsAsync(BaseItem owner, CancellationToken ct) =>
        segmentLookups.IsDue(owner, _libraryManager.GetLibraryOptions(owner))
            ? RunSegmentsAsync(owner, ct)
            : Task.CompletedTask;

    internal async Task RunSegmentProvidersAsync(
        BaseItem owner,
        LibraryOptions libraryOptions,
        CancellationToken ct
    )
    {
        await _mediaSegmentManager
            .RunSegmentPluginProviders(owner, libraryOptions, false, ct)
            .ConfigureAwait(false);
        segmentLookups.SetRan(owner.Id, libraryOptions);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        Guid,
        Task
    > _segmentRuns = new();

    /// <summary>
    /// Runs the segment providers for the row, once at a time. Playback waits at most
    /// <see cref="SegmentWait"/>; a slower provider finishes in the background.
    /// </summary>
    internal async Task RunSegmentsAsync(BaseItem owner, CancellationToken ct)
    {
        var run = _segmentRuns.GetOrAdd(
            owner.Id,
            id =>
                Task.Run(async () =>
                {
                    try
                    {
                        await RunSegmentProvidersAsync(
                                owner,
                                _libraryManager.GetLibraryOptions(owner),
                                CancellationToken.None
                            )
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Segment lookup failed for {Id}", id);
                    }
                })
        );
        _ = run.ContinueWith(
            done => _segmentRuns.TryRemove(new KeyValuePair<Guid, Task>(owner.Id, done)),
            TaskScheduler.Default
        );

        await Task.WhenAny(run, Task.Delay(SegmentWait, ct)).ConfigureAwait(false);
    }
}
