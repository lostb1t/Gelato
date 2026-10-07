using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Gelato.RemuxDb;

/// <summary>
/// Client for RemuxDB, a database of the tracks, codecs and runtimes of files seen on debrid and
/// usenet, keyed by the torrents and NZBs they come from. See <c>/api/openapi.json</c> on the
/// server; the versions lookup is not in the spec.
/// </summary>
/// <remarks>
/// Anonymous: no token is sent. RemuxDB requires an <c>x-client-id</c> header, which is a random
/// id generated for this install (<see cref="Config.PluginConfiguration.RemuxDbClientId"/>) and
/// derived from nothing else.
/// </remarks>
public sealed class RemuxDbClient(HttpClient http, IMemoryCache cache, ILogger<RemuxDbClient> log)
{
    public const string DefaultUrl = "https://remuxdb.1632022.xyz";

    /// <summary>
    /// The whole lookup, retries included: it runs next to the addon's stream request while a
    /// client waits for an item's page.
    /// </summary>
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    // Short: a popular movie has hundreds of versions (2.3 MB of JSON for tt0111161). Stream syncs
    // are cached for StreamTTL anyway, so this only spares repeated syncs close together.
    private static readonly TimeSpan FoundTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NotFoundTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ErrorTtl = TimeSpan.FromMinutes(1);

    internal static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// Every file RemuxDB knows for a movie or episode. Empty when it knows none or cannot be
    /// reached; the result is cached either way.
    /// </summary>
    public async Task<IReadOnlyList<RemuxDbVersion>> GetVersionsAsync(
        RemuxDbTitle title,
        CancellationToken ct
    )
    {
        if (Ahead.TryTake(title.CacheKey) is { } ahead)
            return await ahead.WaitAsync(ct).ConfigureAwait(false);

        return await LookupAsync(title, ct).ConfigureAwait(false);
    }

    // Static: the client is a typed HttpClient, so every service that takes one has its own.
    private static readonly AheadOfTime<IReadOnlyList<RemuxDbVersion>> Ahead = new(
        TimeSpan.FromSeconds(30)
    );

    /// <summary>
    /// Starts a title's lookup before its stream sync asks for it, for that sync to take: it
    /// then waits for neither the addon's streams nor this.
    /// </summary>
    public void StartLookupAhead(RemuxDbTitle title) =>
        Ahead.Start(title.CacheKey, () => LookupAsync(title, CancellationToken.None));

    /// <summary>
    /// Drops the lookups nobody has taken yet: they were asked of the RemuxDB the configuration
    /// named when they started.
    /// </summary>
    public static void ForgetLookupsAhead() => Ahead.Clear();

    private async Task<IReadOnlyList<RemuxDbVersion>> LookupAsync(
        RemuxDbTitle title,
        CancellationToken ct
    )
    {
        var key = CacheKey(title);
        if (cache.TryGetValue(key, out IReadOnlyList<RemuxDbVersion>? cached) && cached is not null)
            return cached;

        // RemuxDB reads season and episode from the id itself (tt0903747:1:1) and ignores
        // ?season=&episode=, which made every episode a 404.
        var id = Uri.EscapeDataString(title.ImdbId);
        if (title.IsEpisode)
            id += string.Create(CultureInfo.InvariantCulture, $":{title.Season}:{title.Episode}");
        var url = $"{BaseUrl()}/api/media/{id}/versions";

        IReadOnlyList<RemuxDbVersion> versions = [];
        var ttl = ErrorTtl;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(LookupTimeout);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddClientId(request);
            using var response = await http.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token
                )
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                ttl = NotFoundTtl;
            }
            else if (!response.IsSuccessStatusCode)
            {
                log.LogWarning(
                    "RemuxDB lookup for {Title} failed with {Status}",
                    title.CacheKey,
                    (int)response.StatusCode
                );
            }
            else
            {
                versions =
                    await response
                        .Content.ReadFromJsonAsync<List<RemuxDbVersion>>(JsonOpts, timeout.Token)
                        .ConfigureAwait(false) ?? [];
                ttl = versions.Count > 0 ? FoundTtl : NotFoundTtl;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning(
                "RemuxDB lookup for {Title} timed out after {Seconds}s",
                title.CacheKey,
                LookupTimeout.TotalSeconds
            );
        }
        // Anything else too (a connection dropped mid-body, a corrupt compressed body, a bad
        // RemuxDbUrl): media info is extra, and a failed lookup must not fail the stream sync.
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("RemuxDB lookup for {Title} failed: {Error}", title.CacheKey, ex.Message);
        }

        log.LogDebug(
            "RemuxDB lookup for {Title}: {Count} versions",
            title.CacheKey,
            versions.Count
        );
        cache.Set(key, versions, ttl);
        return versions;
    }

    /// <summary>Forgets a title's lookup, so a file just submitted is found on the next sync.</summary>
    public void Forget(RemuxDbTitle title)
    {
        cache.Remove(CacheKey(title));
        _ = Ahead.TryTake(title.CacheKey);
    }

    /// <summary>Submits a probed file. Returns whether RemuxDB took it.</summary>
    public async Task<bool> SubmitAsync(RemuxDbSubmission submission, CancellationToken ct)
    {
        submission.ClientId = ClientId();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl()}/api/mediainfo")
        {
            // Buffered, with a Content-Length: JsonContent streams the body chunked.
            Content = new StringContent(
                JsonSerializer.Serialize(submission, JsonOpts),
                Encoding.UTF8,
                "application/json"
            ),
        };
        AddClientId(request);

        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
            return true;

        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        log.LogWarning(
            "RemuxDB rejected the submission for {Filename}: {Status} {Body}",
            submission.Filename,
            (int)response.StatusCode,
            body.Length > 300 ? body[..300] : body
        );
        return false;
    }

    private static string CacheKey(RemuxDbTitle title) => $"remuxdb:{title.CacheKey}";

    private static string BaseUrl()
    {
        var url = GelatoPlugin.Instance?.Configuration.RemuxDbUrl;
        return (string.IsNullOrWhiteSpace(url) ? DefaultUrl : url.Trim()).TrimEnd('/');
    }

    private static void AddClientId(HttpRequestMessage request) =>
        request.Headers.TryAddWithoutValidation("x-client-id", ClientId());

    private static readonly Lock ClientIdLock = new();

    /// <summary>The random id of this install, created on first use.</summary>
    private static string ClientId()
    {
        var plugin = GelatoPlugin.Instance;
        if (plugin is null)
            return Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

        lock (ClientIdLock)
        {
            var cfg = plugin.Configuration;
            if (string.IsNullOrWhiteSpace(cfg.RemuxDbClientId))
            {
                cfg.RemuxDbClientId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                plugin.SaveConfiguration();
            }

            return cfg.RemuxDbClientId;
        }
    }
}
