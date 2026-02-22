// Copyright (C) 2026 IntroDB contributors
// SPDX-License-Identifier: GPL-3.0-only

using System;
using System.Diagnostics;
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
/// Client for retrieving intro timestamps from IntroDB.
/// </summary>
public sealed class IntroDbClient
{
    /// <summary>
    /// Default timeout for IntroDB requests, in seconds.
    /// </summary>
    public const int DefaultTimeoutSeconds = 10;

    private const string BaseUrl = "https://api.introdb.app";
    private const string IntroPath = "/intro";
    private const double MillisecondsPerSecond = 1000d;

    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
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
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Fetch intro timestamps for a specific episode.
    /// </summary>
    /// <param name="imdbId">IMDb id.</param>
    /// <param name="seasonNumber">Season number.</param>
    /// <param name="episodeNumber">Episode number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Intro result or null if not available.</returns>
    public async Task<IntroDbIntroResult?> GetIntroAsync(
        string imdbId,
        int seasonNumber,
        int episodeNumber,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            throw new ArgumentException("IMDb id must be provided.", nameof(imdbId));
        }

        if (seasonNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seasonNumber), "Season number must be positive.");
        }

        if (episodeNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(episodeNumber), "Episode number must be positive.");
        }

        Debug.Assert(!string.IsNullOrWhiteSpace(imdbId), "IMDb id must be provided.");
        Debug.Assert(seasonNumber > 0, "Season number must be positive.");
        Debug.Assert(episodeNumber > 0, "Episode number must be positive.");

        var requestUri = BuildIntroUri(imdbId, seasonNumber, episodeNumber);

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.StatusCode == HttpStatusCode.BadRequest)
        {
            _logger.LogWarning("IntroDB request rejected for {ImdbId} S{Season}E{Episode}.", imdbId, seasonNumber, episodeNumber);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "IntroDB request failed for {ImdbId} S{Season}E{Episode} with status {Status}.",
                imdbId,
                seasonNumber,
                episodeNumber,
                response.StatusCode);
            return null;
        }

#if EMBY
        using var payloadStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#else
        using var payloadStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#endif
        var payload = await JsonSerializer.DeserializeAsync<IntroDbIntroResponse>(
            payloadStream,
            SerializerOptions,
            cancellationToken).ConfigureAwait(false);

        if (payload == null)
        {
            _logger.LogWarning("IntroDB response could not be parsed for {ImdbId} S{Season}E{Episode}.", imdbId, seasonNumber, episodeNumber);
            return null;
        }

        if (payload.EndMs <= payload.StartMs || payload.StartMs < 0)
        {
            _logger.LogWarning(
                "IntroDB returned invalid timing for {ImdbId} S{Season}E{Episode}: {StartMs} - {EndMs}.",
                imdbId,
                seasonNumber,
                episodeNumber,
                payload.StartMs,
                payload.EndMs);
            return null;
        }

        return new IntroDbIntroResult(
            payload.ImdbId,
            payload.Season,
            payload.Episode,
            payload.StartMs / MillisecondsPerSecond,
            payload.EndMs / MillisecondsPerSecond,
            payload.Confidence,
            payload.SubmissionCount);
    }

    private Uri BuildIntroUri(string imdbId, int seasonNumber, int episodeNumber)
    {
        var baseUri = _httpClient.BaseAddress ?? new Uri(BaseUrl, UriKind.Absolute);
        var builder = new UriBuilder(new Uri(baseUri, IntroPath))
        {
            Query = $"imdb_id={Uri.EscapeDataString(imdbId)}&season={seasonNumber}&episode={episodeNumber}"
        };
        return builder.Uri;
    }

    private sealed class IntroDbIntroResponse
    {
        [JsonPropertyName("imdb_id")]
        public string ImdbId { get; set; } = string.Empty;

        [JsonPropertyName("season")]
        public int Season { get; set; }

        [JsonPropertyName("episode")]
        public int Episode { get; set; }

        [JsonPropertyName("start_ms")]
        public long StartMs { get; set; }

        [JsonPropertyName("end_ms")]
        public long EndMs { get; set; }

        [JsonPropertyName("confidence")]
        public double Confidence { get; set; }

        [JsonPropertyName("submission_count")]
        public int SubmissionCount { get; set; }
    }
}

public sealed record IntroDbIntroResult(
    string ImdbId,
    int Season,
    int Episode,
    double StartSeconds,
    double EndSeconds,
    double Confidence,
    int SubmissionCount);