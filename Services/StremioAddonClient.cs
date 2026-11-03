using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>
/// One Stremio addon (AIOStreams), asked over HTTP: it owns the addon's base URL, the timeout, how
/// a resource's URL is built and what a failed request logs. What the answers mean (the manifest's
/// lifetime, the memoised metas, the search catalogs) is <see cref="GelatoStremioProvider"/>'s.
/// </summary>
/// <remarks>
/// A test stubs the addon by overriding <see cref="GetJsonAsync{T}"/>, which every request goes
/// through.
/// </remarks>
public class StremioAddonClient(string baseUrl, IHttpClientFactory http, ILogger<StremioAddonClient> log)
{
    /// <summary>
    /// The User-Agent AIOStreams takes for another AIOStreams, which makes it add its stream data
    /// (the torrent's info hash, the file's size). RemuxDB needs the hash to match and accept a
    /// file; Remux asks the same way. Other addons keep getting Gelato's own.
    /// </summary>
    private const string AioStreamsUserAgent = "AIOStreams/1.0";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>The addon's URL, which carries the user's addon config: never log it as it is.</summary>
    public string BaseUrl => baseUrl;

    private HttpClient NewClient()
    {
        var c = http.CreateClient(nameof(StremioAddonClient));
        c.Timeout = Timeout;
        c.DefaultRequestHeaders.UserAgent.TryParseAdd(GelatoPlugin.UserAgent);
        return c;
    }

    private string BuildUrl(string[] segments, IEnumerable<string>? extras = null)
    {
        var parts = segments.Select(Uri.EscapeDataString).ToArray();
        var path = string.Join("/", parts);

        var extrasPart = string.Empty;
        if (extras != null)
        {
            var enumerable = extras.ToList();
            extrasPart = enumerable.Count != 0 ? "/" + string.Join("&", enumerable) : string.Empty;
        }

        var url = $"{baseUrl}/{path}{extrasPart}.json";
        url = url.Replace("%3A", ":").Replace("%3a", ":");
        return url;
    }

    /// <summary>
    /// <c>GET</c> a URL of the addon and read the JSON. A non-success status throws an
    /// <see cref="HttpRequestException"/> carrying it (logged as a warning); a failure to reach or
    /// parse it is logged as an error and thrown.
    /// </summary>
    protected virtual async Task<T?> GetJsonAsync<T>(string url, string? userAgent = null)
    {
        // The base URL carries the user's addon config, so only the resource after it is logged.
        var resource = url.StartsWith(baseUrl, StringComparison.Ordinal)
            ? url[baseUrl.Length..]
            : Redact.Url(url);
        log.LogDebug(
            "GetJsonAsync: requesting {Resource} from {Addon}",
            resource,
            Redact.Url(baseUrl)
        );

        try
        {
            var c = NewClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (userAgent is not null)
                request.Headers.UserAgent.ParseAdd(userAgent);
            var resp = await c.SendAsync(request).ConfigureAwait(false); // No using statement

            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning(
                    "GetJsonAsync: request failed for {Resource} with {StatusCode} {ReasonPhrase}",
                    resource,
                    resp.StatusCode,
                    resp.ReasonPhrase
                );

                throw new HttpRequestException(
                    $"HTTP {resp.StatusCode}: {resp.ReasonPhrase}",
                    null,
                    resp.StatusCode
                );
            }

            await using var s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(s, JsonOpts).ConfigureAwait(false);
        }
        // A non-success status was logged above; the exception only carries it to the caller.
        catch (Exception ex) when (ex is not HttpRequestException { StatusCode: not null })
        {
            log.LogError(
                ex,
                "GetJsonAsync: error fetching or parsing {Resource} from {Addon}",
                resource,
                Redact.Url(baseUrl)
            );
            throw;
        }
    }

    private static string Kind(StremioMediaType mediaType) => mediaType.ToString().ToLower();

    public Task<StremioManifest?> GetManifestAsync() =>
        GetJsonAsync<StremioManifest>($"{baseUrl}/manifest.json");

    /// <summary>The addon's meta for an id, null when it answers none.</summary>
    public async Task<StremioMeta?> GetMetaAsync(string id, StremioMediaType mediaType)
    {
        var r = await GetJsonAsync<StremioMetaResponse>(BuildUrl(["meta", Kind(mediaType), id]));
        return r?.Meta;
    }

    /// <param name="aioStreams">
    /// Whether the addon is AIOStreams, which is then asked as another AIOStreams would.
    /// </param>
    public async Task<List<StremioStream>> GetStreamsAsync(
        string id,
        StremioMediaType mediaType,
        bool aioStreams
    )
    {
        var r = await GetJsonAsync<StremioStreamsResponse>(
            BuildUrl(["stream", Kind(mediaType), id]),
            aioStreams ? AioStreamsUserAgent : null
        );
        return r?.Streams ?? [];
    }

    public async Task<List<StremioSubtitle>> GetSubtitlesAsync(string id, StremioMediaType mediaType)
    {
        var r = await GetJsonAsync<StremioSubtitleResponse>(
            BuildUrl(["subtitles", Kind(mediaType), id])
        );
        return r.Subtitles;
    }

    public async Task<IReadOnlyList<StremioMeta>> GetCatalogAsync(
        string id,
        string mediaType,
        string? search = null,
        int? skip = null
    )
    {
        var extras = new List<string>();
        if (!string.IsNullOrWhiteSpace(search))
            extras.Add($"search={Uri.EscapeDataString(search)}");
        if (skip is > 0)
            extras.Add($"skip={skip}");

        // seen maybe one type thats capital, but thats their issue
        var r = await GetJsonAsync<StremioCatalogResponse>(
            BuildUrl(["catalog", mediaType.ToLower(), id], extras)
        );
        return r?.Metas ?? [];
    }
}
