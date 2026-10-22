using Gelato.Services;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Gelato.Decorators.MediaSources;

/// <summary>
/// The streams found dead lately, and the stream a playback of a title's default source moved on
/// to when that one did not start.
/// </summary>
internal sealed class StreamFailover(
    MediaSourceManagerDecorator decorator,
    ILogger<MediaSourceManagerDecorator> log
)
{
    private readonly ILogger<MediaSourceManagerDecorator> _log = log;

    /// <summary>How long one stream gets to answer, and then to deliver its first bytes.</summary>
    internal static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long a playback that moved on from a dead stream keeps to its replacement.</summary>
    private static readonly TimeSpan FailoverTtl = TimeSpan.FromHours(12);

    // The stream a playback moved on to, by the dead stream's row. The source keeps the id the
    // client asked for: Jellyfin picks the source out of the answer by that id, in PlaybackInfo
    // and in every streaming request after it.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<
        string,
        (string Row, DateTime At)
    > _failover = new();

    /// <summary>
    /// At the start of a playback: the stream the title plays by default, or the next of its
    /// streams (<see cref="StreamLinks.MaxStreams"/> in all) when it is known not to start: its
    /// pre-probe or its playback's probe found it dead lately (<see cref="MarkDead"/>). Nothing is fetched for that, so a stream
    /// that works starts as fast as before. A version picked by hand (one that is not the first)
    /// and a stream row's own page play as they are. <paramref name="canMoveOn"/> says whether
    /// the playback moves on again when the probe of the stream returned finds no video; when
    /// every stream is known dead, the first plays as it did before.
    /// </summary>
    internal MediaSourceInfo FailOver(
        BaseItem item,
        IReadOnlyList<MediaSourceInfo> sources,
        MediaSourceInfo selected,
        out bool canMoveOn
    )
    {
        canMoveOn = false;
        var key = PreProbes.PreProbeKey(selected);
        _failover.TryRemove(key, out _);
        if (
            !GelatoPlugin.Instance!.Configuration.PlaybackFailover
            || item.IsStremioNonPrimary()
            || !ReferenceEquals(selected, sources[0])
            || StreamLinks.AsHttpUri(selected.Path) is null
        )
        {
            return selected;
        }

        var candidates = sources
            .Where(s => StreamLinks.AsHttpUri(s.Path) is not null)
            .Take(StreamLinks.MaxStreams)
            .ToList();
        var index = candidates.FindIndex(s => !DeadRecently(PreProbes.PreProbeKey(s)));
        if (index < 0)
        {
            _log.LogWarning(
                "None of the first {Count} streams of {Item} started lately; playing the first",
                candidates.Count,
                item.Name
            );
            return selected;
        }

        canMoveOn = true;
        if (index == 0)
            return selected;

        var candidate = candidates[index];
        _failover[key] = (PreProbes.PreProbeKey(candidate), DateTime.UtcNow);
        candidate.Id = selected.Id;
        _log.LogInformation(
            "Playing stream {Index} of {Item}: the streams before it did not start",
            index + 1,
            item.Name
        );
        return candidate;
    }

    /// <summary>
    /// The stream a playback of <paramref name="selected"/> moved on to, under the id that was
    /// asked for; <paramref name="selected"/> itself when it did not.
    /// </summary>
    internal MediaSourceInfo FailedOver(IReadOnlyList<MediaSourceInfo> list, MediaSourceInfo selected)
    {
        var key = PreProbes.PreProbeKey(selected);
        if (!_failover.TryGetValue(key, out var entry))
            return selected;

        var now = DateTime.UtcNow;
        if (now - entry.At >= FailoverTtl)
        {
            _failover.TryRemove(key, out _);
            return selected;
        }

        if (list.FirstOrDefault(s => PreProbes.PreProbeKey(s) == entry.Row) is not { } replacement)
            return selected;

        replacement.Id = selected.Id;
        return replacement;
    }

    // Sources found dead lately: no answer, an error, a body cut short or no video at all. A
    // playback of the title's default source moves on from them (FailOver).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _dead =
        new();

    internal void MarkDead(string key)
    {
        decorator.PreProbes.MarkPreProbeFailed(key);
        foreach (var (staleKey, deadAt) in _dead)
        {
            if (DateTime.UtcNow - deadAt >= PreProbes.PreProbeRetry)
                _dead.TryRemove(staleKey, out _);
        }

        _dead[key] = DateTime.UtcNow;
    }

    private bool DeadRecently(string key) =>
        _dead.TryGetValue(key, out var deadAt) && DateTime.UtcNow - deadAt < PreProbes.PreProbeRetry;
}
