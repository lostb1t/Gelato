using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>
/// The only place that talks to TMDB: its base URL, the API key, the timeout and what a failed
/// request logs. Callers ask for an answer and never build a URL; memoising the answers is theirs.
/// </summary>
/// <remarks>
/// A test stubs the service by overriding <see cref="GetBodyAsync"/>, which every request goes
/// through.
/// </remarks>
public class TmdbClient(IHttpClientFactory http, ILogger<TmdbClient> log)
{
    private const string ApiBase = "https://api.themoviedb.org/3";
    private const string ImageBase = "https://image.tmdb.org/t/p";

    // TMDB's watch providers ride along with the release dates: one request for both.
    private const string MovieAppend = "release_dates,watch/providers";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The URL of a poster as the lists on a remote person or title show it.</summary>
    public static string PosterUrl(string path) => $"{ImageBase}/w500{path}";

    /// <summary>The URL of a backdrop at its original size.</summary>
    public static string BackdropUrl(string path) => $"{ImageBase}/original{path}";

    /// <summary>
    /// The API key from the Jellyfin TMDB plugin if configured, otherwise the built-in default key.
    /// </summary>
    private static string ApiKey()
    {
        const string defaultKey = "4219e299c89411838049ab0dab19ebd5";
        try
        {
            // Avoid a hard compile-time dependency on MediaBrowser.Providers.
            // Jellyfin.Providers is loaded at runtime - grab the key via reflection if available.
            var pluginType = Type.GetType(
                "MediaBrowser.Providers.Plugins.Tmdb.Plugin, Jellyfin.Providers",
                throwOnError: false
            );
            var instance = pluginType?.GetProperty("Instance")?.GetValue(null);
            var cfg = instance?.GetType().GetProperty("Configuration")?.GetValue(instance);
            var key = cfg?.GetType().GetProperty("TmdbApiKey")?.GetValue(cfg) as string;
            return string.IsNullOrWhiteSpace(key) ? defaultKey : key;
        }
        catch
        {
            return defaultKey;
        }
    }

    private static string Language(string? language) =>
        string.IsNullOrWhiteSpace(language) ? "" : $"&language={Uri.EscapeDataString(language)}";

    /// <summary>
    /// The body of <c>GET {api}/{path}?api_key=...{query}</c>. Throws an
    /// <see cref="HttpRequestException"/> carrying the status when TMDB does not answer 2xx.
    /// </summary>
    /// <param name="path">The resource, e.g. <c>movie/603</c>, escaped by the caller.</param>
    /// <param name="query">More query parameters, each starting with <c>&amp;</c>.</param>
    protected virtual async Task<string> GetBodyAsync(
        string path,
        string query,
        CancellationToken cancellationToken
    )
    {
        using var client = http.CreateClient(nameof(TmdbClient));
        client.Timeout = Timeout;
        return await client
            .GetStringAsync($"{ApiBase}/{path}?api_key={ApiKey()}{query}", cancellationToken)
            .ConfigureAwait(false);
    }

    private static string Kind(StremioMediaType mediaType) =>
        mediaType is StremioMediaType.Series ? "tv" : "movie";

    /// <summary>
    /// A movie's details with its release dates and watch providers appended, by TMDB id or imdb
    /// id. Throws when TMDB does not answer (404 when it does not know the movie).
    /// </summary>
    public async Task<TmdbMovieReleaseDates?> GetMovieReleaseDatesAsync(
        string id,
        CancellationToken cancellationToken
    )
    {
        var response = await GetBodyAsync(
                $"movie/{Uri.EscapeDataString(id)}",
                $"&append_to_response={MovieAppend}",
                cancellationToken
            )
            .ConfigureAwait(false);
        return JsonSerializer.Deserialize<TmdbMovieReleaseDates>(response, JsonOpts);
    }

    /// <summary>A series' content ratings, per country. Throws when TMDB does not answer.</summary>
    public async Task<List<TmdbContentRating>> GetContentRatingsAsync(
        string tmdbId,
        CancellationToken cancellationToken
    )
    {
        var response = await GetBodyAsync(
                $"tv/{Uri.EscapeDataString(tmdbId)}/content_ratings",
                "",
                cancellationToken
            )
            .ConfigureAwait(false);
        return JsonSerializer.Deserialize<TmdbResults<TmdbContentRating>>(response, JsonOpts)?.Results
            ?? [];
    }

    /// <summary>
    /// The TMDB id of the series TMDB knows under an imdb id, null when it knows none. Throws when
    /// TMDB does not answer.
    /// </summary>
    public async Task<string?> FindSeriesIdByImdbIdAsync(
        string imdbId,
        CancellationToken cancellationToken
    )
    {
        var found = await GetBodyAsync(
                $"find/{Uri.EscapeDataString(imdbId)}",
                "&external_source=imdb_id",
                cancellationToken
            )
            .ConfigureAwait(false);
        using var doc = JsonDocument.Parse(found);
        return doc.RootElement.TryGetProperty("tv_results", out var tv)
            && tv.GetArrayLength() > 0
            && tv[0].TryGetProperty("id", out var id)
            ? id.GetInt32().ToString(CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// The TMDB id of the movie or series TMDB knows under an imdb or TVDB id
    /// (<paramref name="source"/> <c>imdb_id</c> or <c>tvdb_id</c>, <c>find/{id}</c>), "" when
    /// TMDB knows none, null when it does not answer.
    /// </summary>
    public async Task<string?> FindIdAsync(
        string externalId,
        string source,
        StremioMediaType mediaType,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var response = await GetBodyAsync(
                    $"find/{Uri.EscapeDataString(externalId)}",
                    $"&external_source={source}",
                    cancellationToken
                )
                .ConfigureAwait(false);
            using var doc = JsonDocument.Parse(response);
            var results = mediaType is StremioMediaType.Series ? "tv_results" : "movie_results";
            return
                doc.RootElement.TryGetProperty(results, out var found)
                && found.GetArrayLength() > 0
                && found[0].TryGetProperty("id", out var id)
                ? id.GetInt32().ToString(CultureInfo.InvariantCulture)
                : "";
        }
        catch (Exception ex)
            when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogDebug(
                "FindTmdbId: TMDB did not answer for {Id}: {Error}",
                externalId,
                ex.Message
            );
            return null;
        }
    }

    /// <summary>
    /// One of the ids TMDB lists for a movie or series (<c>external_ids</c>), or null when it
    /// lists none or cannot be asked. TMDB gives the TVDB id as a number.
    /// </summary>
    public async Task<string?> GetExternalIdAsync(
        string tmdbId,
        StremioMediaType mediaType,
        string field,
        CancellationToken cancellationToken
    )
    {
        var kind = Kind(mediaType);
        try
        {
            var response = await GetBodyAsync(
                    $"{kind}/{Uri.EscapeDataString(tmdbId)}/external_ids",
                    "",
                    cancellationToken
                )
                .ConfigureAwait(false);
            using var doc = JsonDocument.Parse(response);
            if (!doc.RootElement.TryGetProperty(field, out var id))
                return null;
            var value = id.ValueKind switch
            {
                JsonValueKind.String => id.GetString(),
                JsonValueKind.Number => id.GetRawText(),
                _ => null,
            };
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (Exception ex)
            when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogDebug(
                ex,
                "GetExternalIdFromTmdb: no {Field} for {Kind} {TmdbId}",
                field,
                kind,
                tmdbId
            );
            return null;
        }
    }

    /// <summary>
    /// A person's movie and series credits (<c>person/{id}/combined_credits</c>), in
    /// <paramref name="language"/> (titles and overviews; TMDB falls back to the original), or
    /// null when TMDB does not answer.
    /// </summary>
    public async Task<TmdbPersonCredits?> GetPersonCreditsAsync(
        string tmdbPersonId,
        string? language,
        CancellationToken cancellationToken
    )
    {
        log.LogDebug("GetPersonCredits: asking TMDB for person {TmdbId}", tmdbPersonId);
        try
        {
            var response = await GetBodyAsync(
                    $"person/{Uri.EscapeDataString(tmdbPersonId)}/combined_credits",
                    Language(language),
                    cancellationToken
                )
                .ConfigureAwait(false);
            return JsonSerializer.Deserialize<TmdbPersonCredits>(response, JsonOpts)
                ?? new TmdbPersonCredits();
        }
        catch (Exception ex)
            when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogWarning(
                "GetPersonCredits: TMDB did not answer for person {TmdbId}: {Error}",
                tmdbPersonId,
                ex.Message
            );
            return null;
        }
    }

    /// <summary>
    /// TMDB's recommendations for a movie or series (the first page of about 20), in
    /// <paramref name="language"/>, or null when TMDB does not answer. A movie can be asked for by
    /// its imdb id too.
    /// </summary>
    public async Task<List<TmdbTitle>?> GetRecommendationsAsync(
        StremioMediaType mediaType,
        string id,
        string? language,
        CancellationToken cancellationToken
    )
    {
        var kind = Kind(mediaType);
        log.LogDebug("GetRecommendations: asking TMDB for {Kind} {Id}", kind, id);
        try
        {
            var response = await GetBodyAsync(
                    $"{kind}/{Uri.EscapeDataString(id)}/recommendations",
                    Language(language),
                    cancellationToken
                )
                .ConfigureAwait(false);
            var titles =
                JsonSerializer.Deserialize<TmdbResults<TmdbTitle>>(response, JsonOpts)?.Results
                ?? [];
            foreach (var title in titles)
                title.MediaType ??= kind;
            return titles;
        }
        // A title TMDB does not know has no recommendations, for good.
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return [];
        }
        catch (Exception ex)
            when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            log.LogDebug(
                "GetRecommendations: TMDB did not answer for {Kind} {Id}: {Error}",
                kind,
                id,
                ex.Message
            );
            return null;
        }
    }
}
