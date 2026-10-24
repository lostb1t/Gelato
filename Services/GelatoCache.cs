using System.Collections.Concurrent;
using Gelato.Config;
using Gelato.Decorators;
using Gelato.RemuxDb;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>
/// What Gelato keeps that comes from its configuration or the addon: the addon client per URL
/// (its manifest with it), each user's configuration with the overrides applied, and the memos
/// that depend on either (<see cref="Memo"/>). A configuration save drops all of it at once
/// (<see cref="Invalidate"/>), so a changed setting takes effect without a restart, and the
/// manifest is read again after <see cref="ManifestLifetime"/>, so a change made in AIOStreams
/// shows up in the end without a save too.
/// </summary>
/// <remarks>
/// What a search result became (its item, folder and meta) is not here: it does not come from the
/// configuration, and a client keeps the result's id in its page long after a save.
/// </remarks>
public sealed class GelatoCache(ItemWriteCounter writes, ILogger<GelatoCache> log)
{
    /// <summary>
    /// How long a manifest is used before it is read again. The first use after it reads it in
    /// the background and goes on with the one it has: a search never waits for the manifest. Long,
    /// because most users share a public AIOStreams instance: twice a day at most, and the Catalogs
    /// tab's Refresh (a save) reads it at once.
    /// </summary>
    public static TimeSpan ManifestLifetime =>
        TimeSpan.FromHours(
            GelatoPlugin.Instance?.Configuration.ManifestLifetimeHours is > 0 and var hours
                ? hours
                : PluginConfiguration.DefaultManifestLifetimeHours
        );

    /// <summary>
    /// When a manifest read that failed is tried again. The manifest there was is used meanwhile;
    /// without one every use tries, as before.
    /// </summary>
    public static readonly TimeSpan ManifestRetry = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, GelatoStremioProvider> _providers = new(
        StringComparer.OrdinalIgnoreCase
    );

    private readonly ConcurrentDictionary<Guid, PluginConfiguration> _userConfigs = new();

    private readonly MemoryCache _memo = new(new MemoryCacheOptions());

    /// <summary>
    /// Memos that a configuration save empties: which titles' streams were synced and their
    /// subtitles, the library folder lookups, RemuxDB and IntroDB answers.
    /// </summary>
    public IMemoryCache Memo => _memo;

    private readonly MemoryCache _tmdb = new(new MemoryCacheOptions());

    /// <summary>
    /// What TMDB answered: titles' recommendations and which TMDB id a title has. A
    /// configuration save keeps them, since nothing Gelato is configured with goes into TMDB's
    /// answers.
    /// </summary>
    public IMemoryCache Tmdb => _tmdb;

    /// <summary>The addon client for <paramref name="baseUrl"/>, one per URL.</summary>
    public GelatoStremioProvider Provider(
        string baseUrl,
        Func<string, GelatoStremioProvider> create
    ) => _providers.GetOrAdd(baseUrl, create);

    // Counts the saves: a configuration built, or streams synced, while one came in may come
    // from the old one.
    private int _generation;

    /// <summary>The saves so far: taken before work whose result goes into <see cref="Memo"/>.</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>
    /// Puts <paramref name="value"/> into <see cref="Memo"/> unless a save came in since
    /// <paramref name="generation"/> (<see cref="Generation"/>): the work it remembers may have
    /// used the old configuration or addon.
    /// </summary>
    public void SetMemo<T>(string key, T value, TimeSpan ttl, int generation)
    {
        lock (_userConfigs)
        {
            if (generation == _generation)
                _memo.Set(key, value, ttl);
        }
    }

    /// <summary>The configuration of <paramref name="userId"/>, built once per save.</summary>
    public PluginConfiguration UserConfig(Guid userId, Func<Guid, PluginConfiguration> build)
    {
        if (_userConfigs.TryGetValue(userId, out var kept))
            return kept;

        var generation = Volatile.Read(ref _generation);
        var built = build(userId);
        lock (_userConfigs)
        {
            // Used for this request, not kept: the next one builds it from the saved configuration.
            return generation == _generation ? _userConfigs.GetOrAdd(userId, built) : built;
        }
    }

    /// <summary>
    /// Drops everything that came from the configuration before <paramref name="cfg"/> and from the
    /// addon as it was: the users' configurations and the memos go, the clients of URLs no longer
    /// configured too, and the others read their manifest again on their next use.
    /// </summary>
    public void Invalidate(PluginConfiguration cfg)
    {
        lock (_userConfigs)
        {
            _generation++;
            _userConfigs.Clear();
            _memo.Clear();
        }

        writes.Bump(); // the unreleased set (GelatoItemRepository) is read again
        RemuxDbClient.ForgetLookupsAhead();

        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in cfg.UserConfigs.Select(u => u.Url).Append(cfg.Url))
        {
            if (!string.IsNullOrWhiteSpace(url))
                urls.Add(new PluginConfiguration { Url = url }.GetBaseUrl());
        }

        foreach (var (url, provider) in _providers)
        {
            if (urls.Contains(url))
                provider.Expire();
            else
                _providers.TryRemove(url, out _);
        }

        log.LogDebug("Cache cleared, {Count} addon client(s) kept", _providers.Count);
    }
}
