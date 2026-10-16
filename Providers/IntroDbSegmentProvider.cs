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
/// Gelato's media segment provider: the intro from IntroDB for episodes and, with RemuxDB on,
/// segments from a stream row's named chapters (Intro, Recap, Credits, Preview).
/// </summary>
/// <remarks>
/// One provider for both, so nothing new has to be enabled in a library. Its <see cref="Name"/>
/// stays "Gelato IntroDB": Jellyfin keys the stored segments and a library's disabled and ordered
/// providers by that name.
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
    /// How long an episode IntroDB had no intro for is not asked again. The providers run for
    /// every stream row that plays, and for a row without segments on every playback.
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
    private readonly IMemoryCache _cache;
    private readonly ILogger<IntroDbSegmentProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroDbSegmentProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="introDbClient">IntroDB client.</param>
    /// <param name="chapters">The rows' chapters, from RemuxDB or the probe of their file.</param>
    /// <param name="cache">Remembers the episodes IntroDB has no intro for.</param>
    /// <param name="logger">Logger.</param>
    public IntroDbSegmentProvider(
        ILibraryManager libraryManager,
        IntroDbClient introDbClient,
        IChapterRepository chapters,
        IMemoryCache cache,
        ILogger<IntroDbSegmentProvider> logger
    )
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(introDbClient);
        ArgumentNullException.ThrowIfNull(chapters);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(logger);

        _libraryManager = libraryManager;
        _introDbClient = introDbClient;
        _chapters = chapters;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Gelato IntroDB";

    private static bool RemuxDbEnabled =>
        GelatoPlugin.Instance?.Configuration.RemuxDbEnabled ?? false;

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

        var segments = video is Episode episode
            ? await GetIntroDbSegmentsAsync(episode, request, cancellationToken)
                .ConfigureAwait(false)
            : Array.Empty<MediaSegmentDto>();

        return UsesChapters(video) ? AddChapterSegments(video, segments) : segments;
    }

    private async Task<IReadOnlyList<MediaSegmentDto>> GetIntroDbSegmentsAsync(
        Episode episode,
        MediaSegmentGenerationRequest request,
        CancellationToken cancellationToken
    )
    {
        if (!TryGetImdbId(episode, out var imdbId))
        {
            _logger.LogDebug(
                "Skipping IntroDB lookup for {ItemId}: IMDb id missing.",
                request.ItemId
            );
            return Array.Empty<MediaSegmentDto>();
        }

        if (!TryGetSeasonEpisodeNumbers(episode, out var seasonNumber, out var episodeNumber))
        {
            _logger.LogDebug(
                "Skipping IntroDB lookup for {ItemId}: invalid season/episode number.",
                request.ItemId
            );
            return Array.Empty<MediaSegmentDto>();
        }

        // Per episode: every stream row of it asks for the same intro.
        var noIntroKey = $"gelato:introdb:none:{imdbId}:{seasonNumber}:{episodeNumber}";
        if (_cache.TryGetValue(noIntroKey, out _))
        {
            _logger.LogDebug(
                "IntroDB had no intro for {ItemId} (IMDb {ImdbId} S{Season}E{Episode}) lately, not asking again.",
                request.ItemId,
                imdbId,
                seasonNumber,
                episodeNumber
            );
            return Array.Empty<MediaSegmentDto>();
        }

        IntroDbIntroResult? result;
        try
        {
            result = await _introDbClient
                .GetIntroAsync(imdbId, seasonNumber, episodeNumber, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "IntroDB lookup failed for {ItemId} (IMDb {ImdbId} S{Season}E{Episode}).",
                request.ItemId,
                imdbId,
                seasonNumber,
                episodeNumber
            );
            return Array.Empty<MediaSegmentDto>();
        }

        if (result is null)
        {
            _cache.Set(noIntroKey, true, NoIntroTtl);
            // Also reached when IntroDB answered with an error, which the client logged already.
            _logger.LogInformation(
                "No intro from IntroDB for {ItemId} (IMDb {ImdbId} S{Season}E{Episode}).",
                request.ItemId,
                imdbId,
                seasonNumber,
                episodeNumber
            );
            return Array.Empty<MediaSegmentDto>();
        }

        var startTicks = (long)(result.StartSeconds * TicksPerSecond);
        var endTicks = (long)(result.EndSeconds * TicksPerSecond);
        if (endTicks <= startTicks)
        {
            _logger.LogWarning("IntroDB returned invalid segment for {ItemId}.", request.ItemId);
            return Array.Empty<MediaSegmentDto>();
        }

        if (
            episode.RunTimeTicks.HasValue
            && episode.RunTimeTicks.Value > 0
            && endTicks > episode.RunTimeTicks.Value
        )
        {
            _logger.LogWarning(
                "IntroDB returned segment beyond duration for {ItemId}.",
                request.ItemId
            );
            return Array.Empty<MediaSegmentDto>();
        }

        return new List<MediaSegmentDto>
        {
            new()
            {
                ItemId = request.ItemId,
                StartTicks = startTicks,
                EndTicks = endTicks,
                Type = MediaSegmentType.Intro,
            },
        };
    }

    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item) =>
        ValueTask.FromResult(item is Episode || (item is Video video && UsesChapters(video)));

    /// <inheritdoc />
    /// <remarks>
    /// Segments are fetched from the IntroDB API on demand and nothing is cached locally,
    /// so there is nothing to prune.
    /// </remarks>
    public Task CleanupExtractedData(Guid itemId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>Gelato's stream rows, with RemuxDB on. A local file is left to other providers.</summary>
    private static bool UsesChapters(Video video) => RemuxDbEnabled && video.HasStreamTag();

    private static Regex ChapterTitle(string pattern) =>
        new($"^(?:{pattern})$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Adds a segment for each named chapter of the row, from its start to the next chapter's (the
    /// runtime for the last), for the types IntroDB gave none of: IntroDB's intro wins.
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

    private bool TryGetImdbId(Episode episode, out string imdbId)
    {
        if (
            episode.SeriesId != Guid.Empty
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
            episode.ProviderIds.TryGetValue(
                MetadataProvider.Imdb.ToString(),
                out var providerImdbId
            ) && !string.IsNullOrWhiteSpace(providerImdbId)
        )
        {
            imdbId = providerImdbId;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(episode.Path))
        {
            var match = ImdbIdRegex.Match(episode.Path);
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
