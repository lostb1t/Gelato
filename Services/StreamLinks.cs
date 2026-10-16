using System.Net.Http.Headers;

namespace Gelato.Services;

/// <summary>
/// Reaching a title's stream links, for the download and the playback that try the next stream
/// when one is dead. No URL goes into a log: stream links carry the debrid key.
/// </summary>
public static class StreamLinks
{
    /// <summary>How many of a title's streams are tried before giving up.</summary>
    public const int MaxStreams = 3;

    // What a playback check reads of a stream: debrid links that close early do so within the
    // first few hundred KiB (lostb1t/Gelato#206 saw 30 to 430 KiB).
    private const int CheckBytes = 512 * 1024;

    public static Uri? AsHttpUri(string? path) =>
        Uri.TryCreate(path, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;

    /// <summary>
    /// The response once its headers are in, or null when the stream gives none within
    /// <paramref name="timeout"/> or cannot be reached. The request's own abort still throws.
    /// </summary>
    public static async Task<HttpResponseMessage?> TryGetAsync(
        HttpClient client,
        Uri uri,
        TimeSpan timeout,
        CancellationToken aborted,
        RangeHeaderValue? range = null
    )
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = range;

        // Disposing the source when the headers are in stops its timer, so the body is not cut.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        cts.CancelAfter(timeout);
        try
        {
            return await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
            when (!aborted.IsCancellationRequested
                && ex is HttpRequestException or OperationCanceledException
            )
        {
            return null;
        }
    }

    /// <summary>
    /// Why the stream would not start: no answer, an error status, or a body that ends before
    /// its first <see cref="CheckBytes"/> (or before the length it announced). Null when it starts.
    /// The headers and the read get <paramref name="timeout"/> each.
    /// </summary>
    public static async Task<string?> CheckAsync(
        HttpClient client,
        Uri uri,
        TimeSpan timeout,
        CancellationToken aborted
    )
    {
        using var resp = await TryGetAsync(
                client,
                uri,
                timeout,
                aborted,
                new RangeHeaderValue(0, CheckBytes - 1)
            )
            .ConfigureAwait(false);
        if (resp is null)
            return "no answer";
        if (!resp.IsSuccessStatusCode)
            return $"status {(int)resp.StatusCode}";

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        cts.CancelAfter(timeout);
        long read = 0;
        try
        {
            await using var body = await resp
                .Content.ReadAsStreamAsync(cts.Token)
                .ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            int n;
            while (
                read < CheckBytes
                && (n = await body.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0
            )
            {
                read += n;
            }
        }
        catch (Exception ex)
            when (!aborted.IsCancellationRequested
                && ex is HttpRequestException or OperationCanceledException or IOException
            )
        {
            return $"stalled after {read} bytes";
        }

        // A file smaller than the check is whole when it ends at the length it announced.
        return read >= CheckBytes || (read > 0 && read == resp.Content.Headers.ContentLength)
            ? null
            : $"ended after {read} bytes";
    }
}
