using Gelato.Decorators;
using Gelato.RemuxDb;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>
/// The memos that come from Gelato's configuration or the addon (<see cref="Memo"/>), kept apart
/// from Jellyfin's shared cache so that a configuration save can empty them, and only them
/// (<see cref="Invalidate"/>).
/// </summary>
/// <remarks>
/// What a search result became (its item, folder and meta) is not here: it does not come from the
/// configuration, and a client keeps the result's id in its page long after a save.
/// </remarks>
public sealed class GelatoCache(ItemWriteCounter writes, ILogger<GelatoCache> log)
{
    private readonly MemoryCache _memo = new(new MemoryCacheOptions());

    /// <summary>
    /// Memos that a configuration save empties: which titles' streams were synced and their
    /// subtitles, the library folder lookups, RemuxDB and IntroDB answers.
    /// </summary>
    public IMemoryCache Memo => _memo;

    /// <summary>Drops everything that came from the configuration before a save.</summary>
    public void Invalidate()
    {
        _memo.Clear();
        writes.Bump(); // the unreleased set (GelatoItemRepository) is read again
        RemuxDbClient.ForgetLookupsAhead();
        log.LogDebug("Configuration memos cleared");
    }
}
