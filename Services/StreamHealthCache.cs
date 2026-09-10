using System.Collections.Concurrent;

namespace Gelato.Services;

public sealed class StreamHealthCache
{
    private readonly ConcurrentDictionary<string, CachedHealth> _health = new();
    private readonly ConcurrentDictionary<Guid, CachedPreference> _preferred = new();

    public bool TryGet(string sourceId, DateTimeOffset now, out StreamHealthResult result)
    {
        if (_health.TryGetValue(sourceId, out var cached) && cached.ExpiresAt > now)
        {
            result = cached.Result;
            return true;
        }

        result = null!;
        return false;
    }

    public void Set(string sourceId, StreamHealthResult result, DateTimeOffset now)
    {
        var ttl = result.IsHealthy ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(10);
        _health[sourceId] = new CachedHealth(result, now.Add(ttl));
    }

    public bool TryGetPreferred(Guid itemId, DateTimeOffset now, out string sourceId)
    {
        if (_preferred.TryGetValue(itemId, out var cached) && cached.ExpiresAt > now)
        {
            sourceId = cached.SourceId;
            return true;
        }

        sourceId = string.Empty;
        return false;
    }

    public void SetPreferred(Guid itemId, string sourceId, DateTimeOffset now) =>
        _preferred[itemId] = new CachedPreference(sourceId, now.AddSeconds(30));

    private sealed record CachedHealth(StreamHealthResult Result, DateTimeOffset ExpiresAt);
    private sealed record CachedPreference(string SourceId, DateTimeOffset ExpiresAt);
}
