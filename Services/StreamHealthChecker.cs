using System.Net;

namespace Gelato.Services;

public sealed record StreamHealthResult(
    bool IsHealthy,
    HttpStatusCode? StatusCode,
    long BytesRead,
    string Reason
);

/// <summary>
/// Performs a small bounded read to reject sources that advertise a large file
/// but close before delivering the first probe window.
/// </summary>
public sealed class StreamHealthChecker(
    HttpClient client,
    TimeSpan timeout,
    int probeBytes = 512 * 1024
)
{
    public async Task<StreamHealthResult> CheckAsync(string? url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            return new(false, null, 0, "invalid-url");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, probeBytes - 1);

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token
            ).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return new(false, response.StatusCode, 0, "http-status");

            await using var body = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            long bytesRead = 0;
            while (bytesRead < probeBytes)
            {
                var requested = (int)Math.Min(buffer.Length, probeBytes - bytesRead);
                var read = await body.ReadAsync(buffer.AsMemory(0, requested), timeoutCts.Token).ConfigureAwait(false);
                if (read == 0)
                    break;
                bytesRead += read;
            }

            if (bytesRead < probeBytes)
                return new(false, response.StatusCode, bytesRead, "premature-eof");

            return new(true, response.StatusCode, bytesRead, "ok");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, null, 0, "timeout");
        }
        catch (HttpRequestException)
        {
            return new(false, null, 0, "request-error");
        }
        catch (IOException)
        {
            return new(false, null, 0, "read-error");
        }
    }
}
