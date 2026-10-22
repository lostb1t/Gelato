// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Gelato.Services;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Gelato.Providers;

/// <summary>
/// Gelato's media segment provider: the intro, recap and outro from IntroDB for episodes, the end
/// credits from IntroDB for movies and, with RemuxDB on, segments from a stream row's named
/// chapters (Intro, Recap, Credits, Preview).
/// </summary>
/// <remarks>
/// One provider for both, so nothing new has to be enabled in a library. Its <see cref="Name"/>
/// stays "Gelato IntroDB": Jellyfin keys the stored segments and a library's disabled and ordered
/// providers by that name. What it found for a row is kept (<see cref="SegmentLookups"/>), so
/// IntroDB is asked once per row and again only after a miss or a failure.
/// </remarks>
public class IntroDbSegmentProvider : IMediaSegmentProvider
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;
    private const string ImdbIdPattern = @"\btt\d{7,8}\b";
    private const string SeasonEpisodePattern = @"S(?<season>\d{1,2})E(?<episode>\d{1,2})";

    private static readonly Regex ImdbIdRegex = new(
        ImdbIdPattern,
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );
    private static readonly Regex SeasonEpisodeRegex = new(
        SeasonEpisodePattern,
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    /// <summary>
    /// How long an episode or movie IntroDB had nothing for is not asked again for another of its rows.
    /// </summary>
    private static readonly TimeSpan NoIntroTtl = TimeSpan.FromHours(6);

    /// <summary>
    /// Chapter titles that mark a segment, matched against the whole title, ignoring case.
    /// </summary>
    private static readonly (Regex Title, MediaSegmentType Type)[] ChapterTypes =
    [
        (
            ChapterTitle(
                @"intro|opening( credits| titles)?|op ?\d*|main titles?|title sequence|g[ée]n[ée]rique( de d[ée]but)?|vorspann|sigla( iniziale)?"
            ),
            MediaSegmentType.Intro
        ),
        (
            ChapterTitle(@"recap|previously( on .*)?|r[ée]sum[ée]|r[üu]ckblick|riassunto"),
            MediaSegmentType.Recap
        ),
        (
            ChapterTitle(
                @"(end |closing )?credits|outro|ending( credits)?|ed ?\d*|end titles|g[ée]n[ée]rique de fin|abspann|titoli di coda|cr[ée]ditos( finales)?"
            ),
            MediaSegmentType.Outro
        ),
        (
            ChapterTitle(@"preview|next episode( preview)?|next time|vorschau|anteprima"),
            MediaSegmentType.Preview
        ),
    ];

    private readonly ILibraryManager _libraryManager;
    private readonly IntroDbClient _introDbClient;
    private readonly IChapterRepository _chapters;
    private readonly SegmentLookups _lookups;
    private readonly IMemoryCache _cache;
    private readonly ILogger<IntroDbSegmentProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroDbSegmentProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="introDbClient">IntroDB client.</param>
    /// <param name="chapters">The rows' chapters, from RemuxDB or the probe of their file.</param>
    /// <param name="lookups">What was found for each row before.</param>
    /// <param name="cache">Remembers the episodes IntroDB has no intro for.</param>
    /// <param name="logger">Logger.</param>
    public IntroDbSegmentProvider(
        ILibraryManager libraryManager,
        IntroDbClient introDbClient,
        IChapterRepository chapters,
        SegmentLookups lookups,
        GelatoCache cache,
        ILogger<IntroDbSegmentProvider> logger
    )
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(introDbClient);
        ArgumentNullException.ThrowIfNull(chapters);
        ArgumentNullException.ThrowIfNull(lookups);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);

        _libraryManager = libraryManager;
        _introDbClient = introDbClient;
        _chapters = chapters;
        _lookups = lookups;
        _cache = cache.Memo;
        _logger = logger;
    }

    /// <summary>The provider's name in a library's segment provider settings.</summary>
    public const string ProviderName = "Gelato IntroDB";

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(
        MediaSegmentGenerationRequest request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        Debug.Assert(
            request.ItemId != Guid.Empty,
            "Media segment request should contain an item id."
        );

        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is not Video video)
        {
            return Array.Empty<MediaSegmentDto>();
        }

        var before = _lookups.Get(video.Id);
        if (before is not null && SegmentLookups.IsLost(before, request.ExistingSegments))
        {
            // A row made again under a deleted one's id: its answer, without its segments.
            _logger.LogDebug(
                "{ItemId} lost the segments IntroDB gave for it, asking again.",
                request.ItemId
            );
            before = null;
        }

        var outcome = SegmentLookups.None;
        IReadOnlyList<MediaSegmentDto> segments = Array.Empty<MediaSegmentDto>();
        if (video is Episode or Movie)
        {
            if (before is not null)
            {
                // Asked lately: what IntroDB gave then is among the row's segments.
                outcome = before.Outcome;
                segments = request
                    .ExistingSegments.Where(s => before.Types.Contains(s.Type))
                    .ToList();
                _logger.LogDebug(
                    "IntroDB was asked for {ItemId} lately ({Outcome}), not asking again.",
                    request.ItemId,
                    outcome
                );
            }
            else
            {
                (outcome, segments) = await GetIntroDbSegmentsAsync(
                        video,
                        request,
                        cancellationToken
                    )
                    .ConfigureAwait(false);

                // Jellyfin replaces the row's segments with what this returns: what was found
                // before stays until IntroDB answers.
                if (outcome == SegmentLookups.Failed)
                    segments = request.ExistingSegments.ToList();
            }
        }

        var chapters = UsesChapters(video);
        var result = chapters ? AddChapterSegments(video, segments) : segments;

        if (before is null || (chapters && !before.Chapters))
        {
            _lookups.Set(
                video.Id,
                new SegmentLookups.Lookup(
                    outcome,
                    segments.Select(s => s.Type).Distinct().ToArray(),
                    chapters || (before?.Chapters ?? false)
                )
            );
        }

        return result;
    }

    private async Task<(
        string Outcome,
        IReadOnlyList<MediaSegmentDto> Segments
    )> GetIntroDbSegmentsAsync(
        Video video,
        MediaSegmentGenerationRequest request,
        CancellationToken cancellationToken
    )
    {
        if (!TryGetImdbId(video, out var imdbId))
        {
            _logger.LogDebug(
                "Skipping IntroDB lookup for {ItemId}: IMDb id missing.",
                request.ItemId
            );
            return (SegmentLookups.Miss, Array.Empty<MediaSegmentDto>());
        }

        var seasonNumber = 0;
        var episodeNumber = 0;
        if (
            video is Episode episode
            && !TryGetSeasonEpisodeNumbers(episode, out seasonNumber, out episodeNumber)
        )
        {
            _logger.LogDebug(
                "Skipping IntroDB lookup for {ItemId}: invalid season/episode number.",
                request.ItemId
            );
            return (SegmentLookups.Miss, Array.Empty<MediaSegmentDto>());
        }

        // Per episode or movie: every stream row of it asks for the same segments.
        var isMovie = video is Movie;
        var title = isMovie ? imdbId : $"{imdbId} S{seasonNumber}E{episodeNumber}";
        var noIntroKey = isMovie
            ? $"gelato:introdb:none:{imdbId}:movie"
            : $"gelato:introdb:none:{imdbId}:{seasonNumber}:{episodeNumber}";
        if (_cache.TryGetValue(noIntroKey, out _))
        {
            _logger.LogDebug(
                "IntroDB had nothing for {ItemId} (IMDb {Title}) lately, not asking again.",
                request.ItemId,
                title
            );
            return (SegmentLookups.Miss, Array.Empty<MediaSegmentDto>());
        }

        IReadOnlyList<IntroDbSegment> found;
        try
        {
            found = await (
                isMovie
                    ? _introDbClient.GetMovieSegmentsAsync(imdbId, cancellationToken)
                    : _introDbClient.GetSegmentsAsync(
                        imdbId,
                        seasonNumber,
                        episodeNumber,
                        cancellationToken
                    )
            ).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A timeout too: asked again in a few minutes.
            _logger.LogWarning(
                "IntroDB lookup failed for {ItemId} (IMDb {Title}): {Error}",
                request.ItemId,
                title,
                exception.Message
            );
            return (SegmentLookups.Failed, Array.Empty<MediaSegmentDto>());
        }

        // A post-credits scene is content: no segment of its own, and the outro stops at it.
        var scenes = found.Where(s => s.Kind == IntroDbSegmentKind.PostCredits).ToList();
        var segments = new List<MediaSegmentDto>();
        foreach (var each in found)
        {
            if (each.Kind == IntroDbSegmentKind.PostCredits)
            {
                continue;
            }

            if (
                (each.Kind == IntroDbSegmentKind.Outro ? OutsideScenes(each, scenes) : each)
                is not { } segment
            )
            {
                continue;
            }

            var startTicks = (long)(segment.StartSeconds * TicksPerSecond);
            var endTicks = (long)(segment.EndSeconds * TicksPerSecond);
            if (video.RunTimeTicks is > 0 && endTicks > video.RunTimeTicks.Value)
            {
                _logger.LogWarning(
                    "IntroDB returned {Kind} beyond duration for {ItemId}.",
                    segment.Kind,
                    request.ItemId
                );
                continue;
            }

            segments.Add(
                new MediaSegmentDto
                {
                    ItemId = request.ItemId,
                    StartTicks = startTicks,
                    EndTicks = endTicks,
                    Type = segment.Kind switch
                    {
                        IntroDbSegmentKind.Recap => MediaSegmentType.Recap,
                        IntroDbSegmentKind.Outro => MediaSegmentType.Outro,
                        _ => MediaSegmentType.Intro,
                    },
                }
            );
        }

        if (segments.Count == 0)
        {
            _cache.Set(noIntroKey, true, NoIntroTtl);
            _logger.LogDebug(
                "No segments from IntroDB for {ItemId} (IMDb {Title}).",
                request.ItemId,
                title
            );
            return (SegmentLookups.Miss, segments);
        }

        return (SegmentLookups.Hit, segments);
    }

    /// <summary>
    /// The outro without the post-credits scenes it overlaps: it ends where a scene in it starts,
    /// or starts where a scene at its start ends. Nothing when no credits are left.
    /// </summary>
    private static IntroDbSegment? OutsideScenes(
        IntroDbSegment outro,
        IReadOnlyList<IntroDbSegment> scenes
    )
    {
        var start = outro.StartSeconds;
        var end = outro.EndSeconds;
        foreach (var scene in scenes)
        {
            if (scene.EndSeconds <= start || scene.StartSeconds >= end)
            {
                continue;
            }

            if (scene.StartSeconds > start)
            {
                end = scene.StartSeconds;
            }
            else
            {
                start = scene.EndSeconds;
            }
        }

        return end > start ? outro with { StartSeconds = start, EndSeconds = end } : null;
    }

    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item) =>
        ValueTask.FromResult(
            item is Episode or Movie || (item is Video video && UsesChapters(video))
        );

    /// <inheritdoc />
    /// <remarks>Forgets what was found for the row, so a row inserted again is looked up.</remarks>
    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken)
    {
        _lookups.Forget(itemId);
        return Task.CompletedTask;
    }

    /// <summary>Gelato's stream rows, with RemuxDB on. A local file is left to other providers.</summary>
    private static bool UsesChapters(Video video) => SegmentLookups.UsesChapters(video);

    private static Regex ChapterTitle(string pattern) =>
        new($"^(?:{pattern})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Adds a segment for each named chapter of the row, from its start to the next chapter's (the
    /// runtime for the last), for the types IntroDB gave none of: IntroDB's segments win.
    /// </summary>
    private IReadOnlyList<MediaSegmentDto> AddChapterSegments(
        Video row,
        IReadOnlyList<MediaSegmentDto> segments
    )
    {
        var chapters = _chapters.GetChapters(row.Id).OrderBy(c => c.StartPositionTicks).ToList();
        if (chapters.Count < 2)
        {
            return segments;
        }

        var taken = segments.Select(s => s.Type).ToHashSet();
        var result = new List<MediaSegmentDto>(segments);
        var added = 0;
        for (var i = 0; i < chapters.Count; i++)
        {
            var title = chapters[i].Name?.Trim();
            if (string.IsNullOrEmpty(title))
            {
                continue;
            }

            var type = ChapterTypes.FirstOrDefault(t => t.Title.IsMatch(title));
            if (type.Title is null || taken.Contains(type.Type))
            {
                continue;
            }

            var start = chapters[i].StartPositionTicks;
            var end =
                i + 1 < chapters.Count ? chapters[i + 1].StartPositionTicks : row.RunTimeTicks ?? 0;
            if (end <= start)
            {
                continue;
            }

            // "Credits" followed by "End Credits": one segment.
            if (
                result.Count > segments.Count
                && result[^1].Type == type.Type
                && result[^1].EndTicks == start
            )
            {
                result[^1].EndTicks = end;
                continue;
            }

            result.Add(
                new MediaSegmentDto
                {
                    ItemId = row.Id,
                    StartTicks = start,
                    EndTicks = end,
                    Type = type.Type,
                }
            );
            added++;
        }

        if (added > 0)
        {
            _logger.LogDebug("{Count} segments from the chapters of {ItemId}.", added, row.Id);
        }

        return result;
    }

    private bool TryGetImdbId(Video video, out string imdbId)
    {
        if (
            video is Episode episode
            && episode.SeriesId != Guid.Empty
            && _libraryManager.GetItemById(episode.SeriesId) is Series series
        )
        {
            if (
                series.ProviderIds.TryGetValue(
                    MetadataProvider.Imdb.ToString(),
                    out var seriesImdbId
                ) && !string.IsNullOrWhiteSpace(seriesImdbId)
            )
            {
                imdbId = seriesImdbId;
                return true;
            }
        }

        if (
            video.ProviderIds.TryGetValue(
                MetadataProvider.Imdb.ToString(),
                out var providerImdbId
            ) && !string.IsNullOrWhiteSpace(providerImdbId)
        )
        {
            imdbId = providerImdbId;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(video.Path))
        {
            var match = ImdbIdRegex.Match(video.Path);
            if (match.Success)
            {
                imdbId = match.Value;
                return true;
            }
        }

        imdbId = string.Empty;
        return false;
    }

    private static bool TryGetSeasonEpisodeNumbers(
        Episode episode,
        out int seasonNumber,
        out int episodeNumber
    )
    {
        seasonNumber = episode.AiredSeasonNumber ?? episode.ParentIndexNumber ?? 0;
        episodeNumber = episode.IndexNumber ?? 0;

        if (seasonNumber > 0 && episodeNumber > 0)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(episode.Path))
        {
            var match = SeasonEpisodeRegex.Match(episode.Path);
            if (
                match.Success
                && int.TryParse(match.Groups["season"].Value, out var parsedSeason)
                && int.TryParse(match.Groups["episode"].Value, out var parsedEpisode)
            )
            {
                seasonNumber = parsedSeason;
                episodeNumber = parsedEpisode;
                return seasonNumber > 0 && episodeNumber > 0;
            }
        }

        return seasonNumber > 0 && episodeNumber > 0;
    }
}
