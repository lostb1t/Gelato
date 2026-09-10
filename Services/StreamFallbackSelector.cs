namespace Gelato.Services;

public static class StreamFallbackSelector
{
    public static async Task<int> SelectAsync<T>(
        IReadOnlyList<T> candidates,
        int startIndex,
        Func<T, Task<bool>> isHealthy,
        CancellationToken cancellationToken,
        int maxParallelism = 1,
        int maxCandidates = int.MaxValue
    )
    {
        var first = Math.Max(0, startIndex);
        var lastExclusive = Math.Min(candidates.Count, first + Math.Max(0, maxCandidates));
        var parallelism = Math.Max(1, maxParallelism);

        for (var batchStart = first; batchStart < lastExclusive; batchStart += parallelism)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var batchEnd = Math.Min(lastExclusive, batchStart + parallelism);
            var checks = Enumerable.Range(batchStart, batchEnd - batchStart)
                .Select(async i => (Index: i, Healthy: await isHealthy(candidates[i]).ConfigureAwait(false)))
                .ToArray();
            var results = await Task.WhenAll(checks).ConfigureAwait(false);

            var healthy = results.FirstOrDefault(result => result.Healthy);
            if (healthy.Healthy)
                return healthy.Index;
        }

        return -1;
    }
}
