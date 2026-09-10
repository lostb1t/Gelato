using System.Net;
using Gelato.Services;
using Xunit;

namespace Gelato.Tests;

public sealed class StreamHealthCheckerTests
{
    [Fact]
    public async Task SelectsFirstHealthyCandidateWithoutProbingLaterCandidates()
    {
        var probes = new List<int>();

        var selected = await StreamFallbackSelector.SelectAsync(
            new[] { 1, 2 },
            0,
            async candidate =>
            {
                probes.Add(candidate);
                await Task.Yield();
                return true;
            },
            CancellationToken.None
        );

        Assert.Equal(0, selected);
        Assert.Equal(new[] { 1 }, probes);
    }

    [Fact]
    public async Task SkipsTruncatedCandidateAndSelectsNextHealthyCandidate()
    {
        var selected = await StreamFallbackSelector.SelectAsync(
            new[] { false, true },
            0,
            candidate => Task.FromResult(candidate),
            CancellationToken.None
        );

        Assert.Equal(1, selected);
    }

    [Fact]
    public async Task ReturnsNoCandidateWhenAllCandidatesFail()
    {
        var selected = await StreamFallbackSelector.SelectAsync(
            new[] { false, false },
            0,
            candidate => Task.FromResult(candidate),
            CancellationToken.None
        );

        Assert.Equal(-1, selected);
    }

    [Fact]
    public void KeepsPreferredHealthySourceForImmediateFollowUp()
    {
        var cache = new StreamHealthCache();
        var itemId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        cache.SetPreferred(itemId, "healthy-source", now);

        Assert.True(cache.TryGetPreferred(itemId, now.AddSeconds(1), out var sourceId));
        Assert.Equal("healthy-source", sourceId);
    }

    [Fact]
    public async Task AcceptsRangeThatDeliversRequestedProbeBytes()
    {
        using var client = CreateClient(HttpStatusCode.PartialContent, 512 * 1024);
        var checker = new StreamHealthChecker(client, TimeSpan.FromSeconds(1), 512 * 1024);

        var result = await checker.CheckAsync("https://provider.invalid/video", CancellationToken.None);

        Assert.True(result.IsHealthy);
        Assert.Equal(HttpStatusCode.PartialContent, result.StatusCode);
        Assert.Equal(512 * 1024, result.BytesRead);
    }

    [Fact]
    public async Task RejectsPrematureEofFromRangeResponse()
    {
        using var client = CreateClient(HttpStatusCode.PartialContent, 433_923);
        var checker = new StreamHealthChecker(client, TimeSpan.FromSeconds(1), 512 * 1024);

        var result = await checker.CheckAsync("https://provider.invalid/video", CancellationToken.None);

        Assert.False(result.IsHealthy);
        Assert.Equal(433_923, result.BytesRead);
        Assert.Equal("premature-eof", result.Reason);
    }

    [Fact]
    public async Task RejectsProviderHttpError()
    {
        using var client = CreateClient(HttpStatusCode.BadGateway, 0);
        var checker = new StreamHealthChecker(client, TimeSpan.FromSeconds(1), 1024 * 1024);

        var result = await checker.CheckAsync("https://provider.invalid/video", CancellationToken.None);

        Assert.False(result.IsHealthy);
        Assert.Equal(HttpStatusCode.BadGateway, result.StatusCode);
        Assert.Equal("http-status", result.Reason);
    }

    [Fact]
    public async Task RejectsTimeoutWithoutThrowing()
    {
        using var client = new HttpClient(new DelayingHandler(TimeSpan.FromSeconds(2)));
        var checker = new StreamHealthChecker(client, TimeSpan.FromMilliseconds(20), 512 * 1024);

        var result = await checker.CheckAsync("https://provider.invalid/video", CancellationToken.None);

        Assert.False(result.IsHealthy);
        Assert.Equal("timeout", result.Reason);
    }

    private static HttpClient CreateClient(HttpStatusCode status, int bytes)
    {
        return new HttpClient(new FixedResponseHandler(status, bytes));
    }

    private sealed class FixedResponseHandler(HttpStatusCode status, int bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new ByteArrayContent(new byte[bytes]),
            };
            if (status == HttpStatusCode.PartialContent)
                response.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(0, bytes - 1, 2_000_000);
            return Task.FromResult(response);
        }
    }

    private sealed class DelayingHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.PartialContent);
        }
    }
}
