// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using IntroDbPlugin.Services;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using Jellyfin.Database.Implementations.Enums;

namespace Gelato.Providers;

/// <summary>
/// IntroDB media segment provider.
/// </summary>
public class IntroDbSegmentProvider : IMediaSegmentProvider
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;
    private const string ImdbIdPattern = @"\btt\d{7,8}\b";
    private const string SeasonEpisodePattern = @"S(?<season>\d{1,2})E(?<episode>\d{1,2})";

    private static readonly Regex ImdbIdRegex = new(ImdbIdPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex SeasonEpisodeRegex = new(SeasonEpisodePattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ILibraryManager _libraryManager;
    private readonly IntroDbClient _introDbClient;
    private readonly ILogger<IntroDbSegmentProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroDbSegmentProvider"/> class.
    /// </summary>
    /// <param name="libraryManager">Library manager.</param>
    /// <param name="introDbClient">IntroDB client.</param>
    /// <param name="logger">Logger.</param>
    public IntroDbSegmentProvider(
        ILibraryManager libraryManager,
        IntroDbClient introDbClient,
        ILogger<IntroDbSegmentProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(libraryManager);
        ArgumentNullException.ThrowIfNull(introDbClient);
        ArgumentNullException.ThrowIfNull(logger);

        _libraryManager = libraryManager;
        _introDbClient = introDbClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "IntroDB";

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(
        MediaSegmentGenerationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Debug.Assert(request.ItemId != Guid.Empty, "Media segment request should contain an item id.");

        var item = _libraryManager.GetItemById(request.ItemId);
        if (item is not Episode episode)
        {
            return Array.Empty<MediaSegmentDto>();
        }

        if (!TryGetImdbId(episode, out var imdbId))
        {
            _logger.LogDebug("Skipping IntroDB lookup for {ItemId}: IMDb id missing.", request.ItemId);
            return Array.Empty<MediaSegmentDto>();
        }

        if (!TryGetSeasonEpisodeNumbers(episode, out var seasonNumber, out var episodeNumber))
        {
            _logger.LogDebug("Skipping IntroDB lookup for {ItemId}: invalid season/episode number.", request.ItemId);
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
                episodeNumber);
            return Array.Empty<MediaSegmentDto>();
        }

        if (result is null)
        {
            _logger.LogDebug(
                "IntroDB returned no intro for {ItemId} (IMDb {ImdbId} S{Season}E{Episode}).",
                request.ItemId,
                imdbId,
                seasonNumber,
                episodeNumber);
            return Array.Empty<MediaSegmentDto>();
        }

        var startTicks = (long)(result.StartSeconds * TicksPerSecond);
        var endTicks = (long)(result.EndSeconds * TicksPerSecond);
        if (endTicks <= startTicks)
        {
            _logger.LogWarning("IntroDB returned invalid segment for {ItemId}.", request.ItemId);
            return Array.Empty<MediaSegmentDto>();
        }

        if (episode.RunTimeTicks.HasValue && episode.RunTimeTicks.Value > 0 && endTicks > episode.RunTimeTicks.Value)
        {
            _logger.LogWarning("IntroDB returned segment beyond duration for {ItemId}.", request.ItemId);
            return Array.Empty<MediaSegmentDto>();
        }

        return new List<MediaSegmentDto>
        {
            new()
            {
                ItemId = request.ItemId,
                StartTicks = startTicks,
                EndTicks = endTicks,
                Type = MediaSegmentType.Intro
            }
        };
    }

    /// <inheritdoc />
    public ValueTask<bool> Supports(BaseItem item) => ValueTask.FromResult(item is Episode);

    private bool TryGetImdbId(Episode episode, out string imdbId)
    {
        if (episode.SeriesId != Guid.Empty && _libraryManager.GetItemById(episode.SeriesId) is Series series)
        {
            if (series.ProviderIds.TryGetValue(MetadataProvider.Imdb.ToString(), out var seriesImdbId) &&
                !string.IsNullOrWhiteSpace(seriesImdbId))
            {
                imdbId = seriesImdbId;
                return true;
            }
        }

        if (episode.ProviderIds.TryGetValue(MetadataProvider.Imdb.ToString(), out var providerImdbId) &&
            !string.IsNullOrWhiteSpace(providerImdbId))
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

    private static bool TryGetSeasonEpisodeNumbers(Episode episode, out int seasonNumber, out int episodeNumber)
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
            if (match.Success &&
                int.TryParse(match.Groups["season"].Value, out var parsedSeason) &&
                int.TryParse(match.Groups["episode"].Value, out var parsedEpisode))
            {
                seasonNumber = parsedSeason;
                episodeNumber = parsedEpisode;
                return seasonNumber > 0 && episodeNumber > 0;
            }
        }

        return seasonNumber > 0 && episodeNumber > 0;
    }
}