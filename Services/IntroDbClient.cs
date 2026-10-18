// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>
/// Client for retrieving intro, recap, outro and post-credits timestamps from IntroDB.
/// </summary>
public sealed class IntroDbClient
{
    /// <summary>
    /// Default timeout for IntroDB requests, in seconds.
    /// </summary>
    public const int DefaultTimeoutSeconds = 10;

    private const string BaseUrl = "https://api.introdb.app";
    private const string SegmentsPath = "/segments";
    private const double MillisecondsPerSecond = 1000d;

    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<IntroDbClient> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroDbClient"/> class.
    /// </summary>
    /// <param name="httpClient">HTTP client.</param>
    /// <param name="logger">Logger.</param>
    public IntroDbClient(HttpClient httpClient, ILogger<IntroDbClient> logger)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _httpClient = httpClient;
        _logger = logger;

        if (_httpClient.BaseAddress is null)
        {
            _httpClient.BaseAddress = new Uri(BaseUrl, UriKind.Absolute);
        }

        _httpClient.DefaultRequestHeaders.Accept.Clear();
        _httpClient.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json")
        );
    }

    /// <summary>
    /// Fetch the intro, recap and outro of a specific episode.
    /// </summary>
    /// <param name="imdbId">IMDb id.</param>
    /// <param name="seasonNumber">Season number.</param>
    /// <param name="episodeNumber">Episode number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The segments IntroDB has, none when it has no entry for the episode.</returns>
    /// <exception cref="HttpRequestException">IntroDB could not be asked or did not answer properly.</exception>
    public Task<IReadOnlyList<IntroDbSegment>> GetSegmentsAsync(
        string imdbId,
        int seasonNumber,
        int episodeNumber,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            throw new ArgumentException("IMDb id must be provided.", nameof(imdbId));
        }

        if (seasonNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(seasonNumber),
                "Season number must be positive."
            );
        }

        if (episodeNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(episodeNumber),
                "Episode number must be positive."
            );
        }

        return FetchAsync(
            $"imdb_id={Uri.EscapeDataString(imdbId)}&season={seasonNumber}&episode={episodeNumber}",
            $"{imdbId} S{seasonNumber}E{episodeNumber}",
            cancellationToken
        );
    }

    /// <summary>
    /// Fetch the end credits and the post-credits scene of a movie.
    /// </summary>
    /// <param name="imdbId">IMDb id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The segments IntroDB has, none when it has no entry for the movie.</returns>
    /// <exception cref="HttpRequestException">IntroDB could not be asked or did not answer properly.</exception>
    public Task<IReadOnlyList<IntroDbSegment>> GetMovieSegmentsAsync(
        string imdbId,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            throw new ArgumentException("IMDb id must be provided.", nameof(imdbId));
        }

        return FetchAsync(
            $"imdb_id={Uri.EscapeDataString(imdbId)}&is_movie=true",
            imdbId,
            cancellationToken
        );
    }

    private async Task<IReadOnlyList<IntroDbSegment>> FetchAsync(
        string query,
        string title,
        CancellationToken cancellationToken
    )
    {
        var baseUri = _httpClient.BaseAddress ?? new Uri(BaseUrl, UriKind.Absolute);
        var requestUri = new UriBuilder(new Uri(baseUri, SegmentsPath)) { Query = query }.Uri;

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }

        // Numbers IntroDB does not take: asking again does not change that.
        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            _logger.LogWarning("IntroDB request rejected for {Title}.", title);
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"IntroDB answered {(int)response.StatusCode}",
                null,
                response.StatusCode
            );
        }

#if EMBY
        using var payloadStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#else
        using var payloadStream = await response
            .Content.ReadAsStreamAsync(cancellationToken)
            .ConfigureAwait(false);
#endif
        IntroDbSegmentsResponse? payload;
        try
        {
            payload = await JsonSerializer
                .DeserializeAsync<IntroDbSegmentsResponse>(
                    payloadStream,
                    SerializerOptions,
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new HttpRequestException("IntroDB's answer could not be read", exception);
        }

        if (payload == null)
        {
            throw new HttpRequestException("IntroDB's answer was empty");
        }

        var segments = new List<IntroDbSegment>();
        foreach (
            var (kind, timing) in new[]
            {
                (IntroDbSegmentKind.Intro, payload.Intro),
                (IntroDbSegmentKind.Recap, payload.Recap),
                (IntroDbSegmentKind.Outro, payload.Outro),
                (IntroDbSegmentKind.PostCredits, payload.PostCredits),
            }
        )
        {
            if (timing is null)
            {
                continue;
            }

            if (timing.EndMs <= timing.StartMs || timing.StartMs < 0)
            {
                _logger.LogWarning(
                    "IntroDB returned invalid {Kind} timing for {Title}: {StartMs} - {EndMs}.",
                    kind,
                    title,
                    timing.StartMs,
                    timing.EndMs
                );
                continue;
            }

            segments.Add(
                new IntroDbSegment(
                    kind,
                    timing.StartMs / MillisecondsPerSecond,
                    timing.EndMs / MillisecondsPerSecond
                )
            );
        }

        return segments;
    }

    private sealed class IntroDbSegmentsResponse
    {
        [JsonPropertyName("intro")]
        public IntroDbTiming? Intro { get; set; }

        [JsonPropertyName("recap")]
        public IntroDbTiming? Recap { get; set; }

        [JsonPropertyName("outro")]
        public IntroDbTiming? Outro { get; set; }

        [JsonPropertyName("post_credits")]
        public IntroDbTiming? PostCredits { get; set; }
    }

    private sealed class IntroDbTiming
    {
        [JsonPropertyName("start_ms")]
        public long StartMs { get; set; }

        [JsonPropertyName("end_ms")]
        public long EndMs { get; set; }
    }
}

public enum IntroDbSegmentKind
{
    Intro,
    Recap,
    Outro,

    /// <summary>A scene after (or amid) the end credits: content, never skipped.</summary>
    PostCredits,
}

public sealed record IntroDbSegment(IntroDbSegmentKind Kind, double StartSeconds, double EndSeconds);
