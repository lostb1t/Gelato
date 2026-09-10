namespace Gelato.Services;

public static class StreamFallbackSelector
{
    public static async Task<int> SelectAsync<T>(
        IReadOnlyList<T> candidates,
        int startIndex,
        Func<T, Task<bool>> isHealthy,
        CancellationToken cancellationToken
    )
    {
        for (var i = Math.Max(0, startIndex); i < candidates.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await isHealthy(candidates[i]).ConfigureAwait(false))
                return i;
        }

        return -1;
    }
}
