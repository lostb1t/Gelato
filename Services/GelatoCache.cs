using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Gelato.Config;
using Gelato.Decorators;
using Gelato.RemuxDb;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Gelato.Services;

/// <summary>
/// A named part of <see cref="GelatoCache"/>: how long its entries live, and whether a
/// configuration save empties it.
/// </summary>
/// <param name="Name">The name in logs and counts, and the prefix of its keys.</param>
/// <param name="Ttl">How long an entry lives unless a <c>Set</c> names another.</param>
/// <param name="ClearedBySave">Whether the entries come from the configuration or the addon.</param>
public sealed record CacheRegion(string Name, TimeSpan Ttl, bool ClearedBySave);

/// <summary>
/// Everything Gelato remembers in memory: one size-limited <see cref="MemoryCache"/> with named
/// <see cref="CacheRegion"/>s, each with its lifetime and whether a configuration save empties it,
/// plus the addon client per URL (its manifest with it) and each user's configuration with the
/// overrides applied. A save drops what came from the configuration at once
/// (<see cref="Invalidate"/>), so a changed setting takes effect without a restart, and the
/// manifest is read again after <see cref="ManifestLifetime"/>, so a change made in AIOStreams
/// shows up in the end without a save too.
/// </summary>
/// <remarks>
/// What outlives a save: TMDB's answers (nothing Gelato is configured with goes into them), what a
/// search result became (its item, folder and meta: a client keeps the result's id in its page
/// long after a save), the subtitles, the failed images and the probes' bookkeeping. Not in here:
/// tasks and locks under way, per-request state and <c>AheadOfTime</c>. Every entry counts one
/// against <see cref="SizeLimit"/>; a full cache makes room, or turns a new entry away, so a region
/// is a shortcut and never the only copy of something.
/// </remarks>
public sealed class GelatoCache(ItemWriteCounter writes, ILogger<GelatoCache> log)
{
    /// <summary>Entries the cache holds before it makes room.</summary>
    public const int SizeLimit = 100_000;

    // Emptied by a configuration save: they hold what the addon or the configuration answered.

    /// <summary>The addon's metas, per addon URL and id.</summary>
    public static readonly CacheRegion Addon = new("addon", TimeSpan.FromMinutes(5), true);

    /// <summary>Which titles' streams were synced and when (<c>StreamTTL</c>).</summary>
    public static readonly CacheRegion StreamSync = new("stream-sync", TimeSpan.FromMinutes(5), true);

    /// <summary>The library folder lookups and the seed-file warnings that go with them.</summary>
    public static readonly CacheRegion Folders = new("folders", TimeSpan.FromSeconds(10), true);

    /// <summary>RemuxDB's answers and which probes were sent to it.</summary>
    public static readonly CacheRegion RemuxDb = new("remuxdb", TimeSpan.FromMinutes(10), true);

    /// <summary>The episodes IntroDB has no segments for.</summary>
    public static readonly CacheRegion IntroDb = new("introdb", TimeSpan.FromHours(1), true);

    /// <summary>TMDB's lists prepared with the library's matches.</summary>
    public static readonly CacheRegion TitleLists = new("title-lists", TimeSpan.FromHours(1), true);

    // Kept over a save.

    /// <summary>What TMDB answered: release info, content ratings, credits and recommendations.</summary>
    public static readonly CacheRegion Tmdb = new("tmdb", TimeSpan.FromHours(12), false);

    /// <summary>
    /// Ids TMDB maps to each other. A title's ids do not change: a long lifetime, not none, since a
    /// mapping TMDB lacks today may exist next month.
    /// </summary>
    public static readonly CacheRegion TmdbIds = new("tmdb-ids", TimeSpan.FromDays(30), false);

    /// <summary>The addon's subtitle lists and the entries in them.</summary>
    public static readonly CacheRegion Subtitles = new("subtitles", TimeSpan.FromHours(1), false);

    /// <summary>What a search result became: its meta, the item, the folder and the copy flag.</summary>
    public static readonly CacheRegion Search = new("search", TimeSpan.FromHours(6), false);

    /// <summary>The stream syncs a reset made stale (<c>StreamTTL</c>).</summary>
    public static readonly CacheRegion StreamSyncReset = new(
        "stream-sync-reset",
        TimeSpan.FromMinutes(5),
        false
    );

    /// <summary>Remote images that failed, and until when they are left alone.</summary>
    public static readonly CacheRegion Images = new("images", TimeSpan.FromMinutes(5), false);

    /// <summary>Probes and failover: which sources were found dead or prepared lately.</summary>
    public static readonly CacheRegion Probes = new("probes", TimeSpan.FromMinutes(30), false);

    /// <summary>When a series was last synced by its metadata refresh.</summary>
    public static readonly CacheRegion SeriesSync = new("series-sync", TimeSpan.FromHours(1), false);

    /// <summary>The binge group each user's next episode follows.</summary>
    public static readonly CacheRegion BingeGroups = new("binge-groups", TimeSpan.FromHours(24), false);

    private static readonly CacheRegion[] AllRegions =
    [
        Addon,
        StreamSync,
        Folders,
        RemuxDb,
        IntroDb,
        TitleLists,
        Tmdb,
        TmdbIds,
        Subtitles,
        Search,
        StreamSyncReset,
        Images,
        Probes,
        SeriesSync,
        BingeGroups,
    ];

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

    private readonly MemoryCache _entries = new(
        new MemoryCacheOptions { SizeLimit = SizeLimit, CompactionPercentage = 0.1 }
    );

    // The entries of the regions a save empties expire with this token, which a save replaces.
    private CancellationTokenSource _saved = new();

    private readonly ConcurrentDictionary<string, Counter> _counts = new();

    // Lookups under way, so a second caller of the same key waits for the first one's answer.
    private readonly ConcurrentDictionary<string, Lazy<Task>> _asking = new();

    private sealed class Counter
    {
        public int Entries;
        public long Hits;
        public long Misses;
    }

    private Counter CountOf(CacheRegion region) =>
        _counts.GetOrAdd(region.Name, _ => new Counter());

    private static string Full(CacheRegion region, string key) => region.Name + "|" + key;

    /// <summary>
    /// The value under <paramref name="key"/> in <paramref name="region"/>, if it is there. A null
    /// that was stored is found, with nothing in it.
    /// </summary>
    public bool TryGet<T>(CacheRegion region, string key, [MaybeNullWhen(false)] out T value)
    {
        var counter = CountOf(region);
        if (_entries.TryGetValue(Full(region, key), out var stored))
        {
            if (stored is null)
            {
                Interlocked.Increment(ref counter.Hits);
                value = default!;
                return true;
            }

            if (stored is T typed)
            {
                Interlocked.Increment(ref counter.Hits);
                value = typed;
                return true;
            }
        }

        Interlocked.Increment(ref counter.Misses);
        value = default!;
        return false;
    }

    /// <summary>Whether <paramref name="key"/> is in <paramref name="region"/>.</summary>
    public bool Contains(CacheRegion region, string key) => TryGet<object>(region, key, out _);

    /// <summary>
    /// Puts <paramref name="value"/> under <paramref name="key"/> in <paramref name="region"/> for
    /// <paramref name="ttl"/> (the region's own when null).
    /// </summary>
    public void Set<T>(CacheRegion region, string key, T value, TimeSpan? ttl = null)
    {
        lock (_userConfigs)
            Store(region, key, value, ttl);
    }

    /// <summary>
    /// <see cref="Set{T}(CacheRegion, string, T, TimeSpan?)"/> unless a save came in since
    /// <paramref name="generation"/> (<see cref="Generation"/>): the work it remembers may have
    /// used the old configuration or addon.
    /// </summary>
    public void Set<T>(CacheRegion region, string key, T value, TimeSpan? ttl, int generation)
    {
        lock (_userConfigs)
        {
            if (generation == _generation)
                Store(region, key, value, ttl);
        }
    }

    // Called under the lock a save takes too, so an entry never gets a token that is spent.
    private void Store<T>(CacheRegion region, string key, T value, TimeSpan? ttl)
    {
        var counter = CountOf(region);
        var options = new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = ttl ?? region.Ttl,
        };
        if (region.ClearedBySave)
            options.AddExpirationToken(new CancellationChangeToken(_saved.Token));
        options.RegisterPostEvictionCallback(
            static (_, _, _, state) => Interlocked.Decrement(ref ((Counter)state!).Entries),
            counter
        );

        _entries.Set(Full(region, key), value, options);
        Interlocked.Increment(ref counter.Entries);
    }

    /// <summary>Forgets <paramref name="key"/>.</summary>
    public void Remove(CacheRegion region, string key) => _entries.Remove(Full(region, key));

    /// <summary>
    /// The value under <paramref name="key"/>, asked for with <paramref name="create"/> when it is
    /// not there. A second caller of the same key while the first one asks waits for its answer
    /// instead of asking again. An answer <paramref name="remember"/> refuses (null unless named)
    /// is handed back but not kept, so a failed request is asked for again.
    /// </summary>
    public async Task<T?> GetOrAddAsync<T>(
        CacheRegion region,
        string key,
        Func<Task<T?>> create,
        Func<T?, bool>? remember = null,
        TimeSpan? ttl = null,
        CancellationToken ct = default
    )
    {
        if (TryGet<T>(region, key, out var known))
            return known;

        var full = Full(region, key);
        var generation = Generation;
        var mine = false;

        async Task<T?> Ask()
        {
            mine = true;
            try
            {
                return await Keep(await create().ConfigureAwait(false)).ConfigureAwait(false);
            }
            finally
            {
                _asking.TryRemove(full, out _);
            }
        }

        Task<T?> Keep(T? value)
        {
            if (remember?.Invoke(value) ?? value is not null)
                Set(region, key, value, ttl, generation);
            return Task.FromResult(value);
        }

        var asking = _asking.GetOrAdd(
            full,
            _ => new Lazy<Task>(Ask, LazyThreadSafetyMode.ExecutionAndPublication)
        );
        try
        {
            return await ((Task<T?>)asking.Value).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (!mine && !ct.IsCancellationRequested)
        {
            // The caller that asked failed or gave up: this one asks for itself.
            return await Keep(await create().ConfigureAwait(false)).ConfigureAwait(false);
        }
    }

    /// <summary>What each region holds and how often it answered: for a trace or a debug line.</summary>
    public IReadOnlyDictionary<string, (int Entries, long Hits, long Misses)> Counts() =>
        AllRegions.ToDictionary(
            r => r.Name,
            r =>
            {
                var c = CountOf(r);
                return (Volatile.Read(ref c.Entries), Interlocked.Read(ref c.Hits), Interlocked.Read(ref c.Misses));
            }
        );

    private string CountsLine() =>
        string.Join(
            ", ",
            Counts().Where(c => c.Value.Entries > 0).Select(c => $"{c.Key}={c.Value.Entries}")
        );

    /// <summary>The addon client for <paramref name="baseUrl"/>, one per URL.</summary>
    public GelatoStremioProvider Provider(
        string baseUrl,
        Func<string, GelatoStremioProvider> create
    ) => _providers.GetOrAdd(baseUrl, create);

    // Counts the saves: a configuration built, or streams synced, while one came in may come
    // from the old one.
    private int _generation;

    /// <summary>The saves so far: taken before work whose result goes into a cleared region.</summary>
    public int Generation => Volatile.Read(ref _generation);

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
    /// addon as it was: the users' configurations and the regions a save empties go, the clients
    /// of URLs no longer configured too, and the others read their manifest again on their next
    /// use.
    /// </summary>
    public void Invalidate(PluginConfiguration cfg)
    {
        CancellationTokenSource spent;
        lock (_userConfigs)
        {
            _generation++;
            _userConfigs.Clear();
            spent = _saved;
            _saved = new CancellationTokenSource();
        }

        spent.Cancel();
        spent.Dispose();

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

        log.LogDebug(
            "Cache cleared, {Count} addon client(s) kept, still held: {Held}",
            _providers.Count,
            CountsLine()
        );
    }

    /// <summary>
    /// Forgets every region, what search results became too: after a purge the items they name
    /// are gone. A save empties only the regions that come from the configuration
    /// (<see cref="Invalidate"/>).
    /// </summary>
    public void Clear(PluginConfiguration cfg)
    {
        _entries.Compact(1.0);
        Invalidate(cfg);
    }
}
