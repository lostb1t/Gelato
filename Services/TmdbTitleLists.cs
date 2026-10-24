using System.Collections.Concurrent;
using Gelato.Decorators;
using Gelato.Filters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>
/// Lists of TMDB titles as metas ready to stand in for them: a movie's or series' recommendations
/// and a person's films, their release dates and imdb ids looked up, and their ratings where a
/// user's parental control needs them. Never waited for: a list is answered as kept, and one
/// that is missing or old is prepared in the background, for the next request.
/// </summary>
/// <remarks>
/// TMDB's own lists are kept in <see cref="GelatoCache.Tmdb"/> for <see cref="ListLifetime"/>,
/// the same for every user (a person's credits in <see cref="GelatoStremioProvider"/>). The
/// prepared metas depend on the addon (whether it takes tmdb: ids), so they are in
/// <see cref="GelatoCache.Memo"/>, which a save empties, keyed by its URL, and prepared again
/// after <see cref="Freshness"/>: a movie's digital release date can come meanwhile. The old ones
/// are answered until the new ones are there. Which of the titles the library holds is worked out
/// with them, and again in the background once an item was written since: a request asks the
/// database nothing (90 titles of a Suggestions tab cost 14 ms there).
/// </remarks>
public sealed class TmdbTitleLists(
    GelatoCache cache,
    RemoteTitles remoteTitles,
    GelatoManager manager,
    SearchActionFilter search,
    ItemWriteCounter writes,
    ILogger<TmdbTitleLists> log
)
{
    /// <summary>
    /// A list as kept: the metas, each a copy, and by meta id the library's items that hold the
    /// title, as found after the item writes counted at <c>HeldAt</c>.
    /// </summary>
    public sealed record TitleList(
        IReadOnlyList<StremioMeta> Metas,
        IReadOnlyDictionary<string, Guid[]> Held
    );

    /// <summary>How long TMDB's recommendations of a title are kept: they change slowly.</summary>
    public static readonly TimeSpan ListLifetime = TimeSpan.FromDays(7);

    /// <summary>How old prepared metas may get before they are prepared again.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromHours(12);

    // A preparation is in the background, so it can wait for TMDB longer than a page would.
    private static readonly TimeSpan PrepareBudget = TimeSpan.FromSeconds(30);

    // At most this many lists are prepared at once: browsing past many titles queues them.
    private static readonly SemaphoreSlim Gate = new(2);

    private readonly ConcurrentDictionary<string, Task> _preparing = new();

    private sealed record Prepared(
        IReadOnlyList<StremioMeta> Metas,
        DateTime At,
        bool WithRatings,
        IReadOnlyDictionary<string, Guid[]> Held,
        long HeldAt
    );

    private sealed record Title(StremioMediaType Type, Dictionary<string, string> Ids, string Key);

    /// <summary>
    /// TMDB's recommendations for <paramref name="item"/> as kept, each a copy, or null when none
    /// are kept yet. Prepares them in the background when they are missing, old, or lack the
    /// ratings <paramref name="withRatings"/> asks for.
    /// </summary>
    public TitleList? Recommendations(
        BaseItem item,
        GelatoStremioProvider stremio,
        bool withRatings
    )
    {
        if (TitleOf(item) is not { } title)
            return null;

        return Get(
            $"tmdb-recommendations|{stremio.AddonUrl}|{title.Key}|{remoteTitles.MetadataLanguage()}",
            title.Key,
            ct => RecommendationsAsync(title, stremio, ct),
            stremio,
            withRatings
        );
    }

    /// <summary>
    /// The movies of <paramref name="person"/> on TMDB as kept, each a copy, most popular first:
    /// the ones they directed, or the roles they played (not appearances as themselves). Null
    /// when none are kept yet; prepared in the background as <see cref="Recommendations"/> are.
    /// </summary>
    public TitleList? PersonMovies(
        Person person,
        bool directed,
        GelatoStremioProvider stremio,
        bool withRatings
    )
    {
        if (person.GetProviderId(MetadataProvider.Tmdb) is not { Length: > 0 } tmdbId)
            return null;

        var what = directed ? "directed" : "played";
        return Get(
            $"tmdb-person|{stremio.AddonUrl}|{tmdbId}|{what}|{remoteTitles.MetadataLanguage()}",
            $"person {tmdbId} {what}",
            ct => PersonMoviesAsync(tmdbId, person.Name, directed, stremio, ct),
            stremio,
            withRatings
        );
    }

    private TitleList? Get(
        string key,
        string what,
        Func<CancellationToken, Task<List<StremioMeta>?>> fetch,
        GelatoStremioProvider stremio,
        bool withRatings
    )
    {
        var prepared = cache.Memo.Get<Prepared>(key);
        if (
            prepared is null
            || prepared.At + Freshness < DateTime.UtcNow
            || (withRatings && !prepared.WithRatings)
        )
            Start(key, what, fetch, stremio, withRatings || prepared?.WithRatings == true);
        else if (prepared.HeldAt != writes.Version)
            StartMatch(key, prepared);

        return prepared is null
            ? null
            : new TitleList([.. prepared.Metas.Select(m => m.Copy())], prepared.Held);
    }

    private static Title? TitleOf(BaseItem item)
    {
        var type = item switch
        {
            Movie => StremioMediaType.Movie,
            Series => StremioMediaType.Series,
            _ => StremioMediaType.Unknown,
        };
        if (type == StremioMediaType.Unknown)
            return null;

        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (
            var provider in new[]
            {
                MetadataProvider.Tmdb,
                MetadataProvider.Imdb,
                MetadataProvider.Tvdb,
            }
        )
        {
            if (item.GetProviderId(provider) is { Length: > 0 } value)
                ids[provider.ToString()] = value;
        }

        var first = ids.FirstOrDefault();
        return first.Key is null ? null : new Title(type, ids, $"{type}:{first.Key}:{first.Value}");
    }

    private void Start(
        string key,
        string what,
        Func<CancellationToken, Task<List<StremioMeta>?>> fetch,
        GelatoStremioProvider stremio,
        bool withRatings
    )
    {
        if (_preparing.ContainsKey(key))
            return;

        var generation = cache.Generation;
        var task = new Task<Task>(async () =>
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                var metas = await fetch(cts.Token).ConfigureAwait(false);
                if (metas is null)
                    return;

                await PrepareAsync(metas, stremio, withRatings, cts.Token).ConfigureAwait(false);
                var heldAt = writes.Version;
                var held = await HeldAsync(metas, cts.Token).ConfigureAwait(false);
                cache.SetMemo(
                    key,
                    new Prepared(metas, DateTime.UtcNow, withRatings, held, heldAt),
                    ListLifetime,
                    generation
                );
                log.LogDebug("TMDB titles of {What}: {Count} prepared", what, metas.Count);
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "TMDB titles of {What}: preparing failed", what);
            }
            finally
            {
                Gate.Release();
                _preparing.TryRemove(key, out _);
            }
        });

        if (_preparing.TryAdd(key, task))
            task.Start(TaskScheduler.Default);
    }

    /// <summary>
    /// Works out again which titles of a kept list the library holds, in the background, the list
    /// answered as it is meanwhile.
    /// </summary>
    private void StartMatch(string key, Prepared prepared)
    {
        var matchKey = "held|" + key;
        if (_preparing.ContainsKey(matchKey))
            return;

        var generation = cache.Generation;
        var task = new Task<Task>(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
                var heldAt = writes.Version;
                var held = await HeldAsync(prepared.Metas, cts.Token).ConfigureAwait(false);
                cache.SetMemo(
                    key,
                    prepared with { Held = held, HeldAt = heldAt },
                    ListLifetime,
                    generation
                );
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "TMDB titles: finding the library's items failed");
            }
            finally
            {
                _preparing.TryRemove(matchKey, out _);
            }
        });

        if (_preparing.TryAdd(matchKey, task))
            task.Start(TaskScheduler.Default);
    }

    /// <summary>By meta id, the library's movies and series that hold the title.</summary>
    private async Task<IReadOnlyDictionary<string, Guid[]>> HeldAsync(
        IReadOnlyList<StremioMeta> metas,
        CancellationToken ct
    )
    {
        var titles = metas
            .Select(m => (Meta: m, Item: manager.IntoBaseItem(m.Copy())))
            .Where(t => t.Item is not null)
            .ToList();
        var library = await search
            .FindLibraryItemsAsync(titles.Select(t => t.Item!), ct, titlesOnly: true)
            .ConfigureAwait(false);
        var held = new Dictionary<string, Guid[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (meta, item) in titles)
        {
            if (SearchActionFilter.FindMatches(library, item!) is { Count: > 0 } matches)
                held[meta.Id] = [.. matches.Select(m => m.Id)];
        }

        return held;
    }

    private async Task PrepareAsync(
        List<StremioMeta> metas,
        GelatoStremioProvider stremio,
        bool withRatings,
        CancellationToken ct
    )
    {
        await remoteTitles
            .EnrichReleaseDatesAsync(metas, stremio, PrepareBudget, ct)
            .ConfigureAwait(false);
        await remoteTitles.ApplyImdbIdsAsync(metas, stremio, PrepareBudget, ct).ConfigureAwait(false);
        if (withRatings)
        {
            await Parallel
                .ForEachAsync(
                    metas.Where(ParentalRatings.NeedsLookup),
                    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                    async (m, t) =>
                        await ParentalRatings.LookupAsync(m, stremio, t).ConfigureAwait(false)
                )
                .ConfigureAwait(false);
        }
    }

    private async Task<List<StremioMeta>?> RecommendationsAsync(
        Title title,
        GelatoStremioProvider stremio,
        CancellationToken ct
    )
    {
        var tmdbId = await TmdbIdAsync(title, stremio, ct).ConfigureAwait(false);
        if (tmdbId is null)
            return null;
        if (tmdbId.Length == 0)
            return [];

        var language = remoteTitles.MetadataLanguage();
        var listKey = $"recommendations|{title.Type}:{tmdbId}|{language}";
        if (!cache.Tmdb.TryGetValue<List<TmdbTitle>>(listKey, out var list) || list is null)
        {
            list = await stremio
                .GetRecommendationsAsync(title.Type, tmdbId, language, ct)
                .ConfigureAwait(false);
            if (list is null)
                return null;
            cache.Tmdb.Set(listKey, list, ListLifetime);
        }

        return list.Where(t => !t.Adult)
            .Select(RemoteTitles.ToMeta)
            .Where(m => m is not null && m.Type == title.Type)
            .Select(m => m!)
            .ToList();
    }

    private async Task<List<StremioMeta>?> PersonMoviesAsync(
        string tmdbId,
        string? name,
        bool directed,
        GelatoStremioProvider stremio,
        CancellationToken ct
    )
    {
        var credits = await stremio
            .GetPersonCreditsAsync(tmdbId, remoteTitles.MetadataLanguage(), ct)
            .ConfigureAwait(false);
        if (credits is null)
            return null;

        return PersonActionFilter
            .Filmography(credits, name)
            .Where(c =>
                c.MediaType == "movie"
                && (
                    directed
                        ? string.Equals(c.Job, "Director", StringComparison.OrdinalIgnoreCase)
                        : c.Job is null
                )
            )
            .Select(RemoteTitles.ToMeta)
            .Where(m => m is not null)
            .Select(m => m!)
            .ToList();
    }

    /// <summary>
    /// The title's TMDB id, "" when TMDB has none, null when it did not answer. A movie's
    /// recommendations can be asked for by its imdb id; a series needs TMDB's id, found once.
    /// </summary>
    private async Task<string?> TmdbIdAsync(
        Title title,
        GelatoStremioProvider stremio,
        CancellationToken ct
    )
    {
        if (title.Ids.TryGetValue(nameof(MetadataProvider.Tmdb), out var tmdbId))
            return tmdbId;
        if (
            title.Type == StremioMediaType.Movie
            && title.Ids.TryGetValue(nameof(MetadataProvider.Imdb), out var imdbId)
        )
            return imdbId;

        var (source, value) = title.Ids.TryGetValue(nameof(MetadataProvider.Imdb), out var imdb)
            ? ("imdb_id", imdb)
            : title.Ids.TryGetValue(nameof(MetadataProvider.Tvdb), out var tvdb)
                ? ("tvdb_id", tvdb)
                : (null, null);
        if (source is null)
            return "";

        var key = $"tmdb-id|{title.Type}|{source}:{value}";
        if (cache.Tmdb.TryGetValue<string>(key, out var known) && known is not null)
            return known;

        var found = await stremio
            .FindTmdbIdAsync(value!, source, title.Type, ct)
            .ConfigureAwait(false);
        if (found is not null)
            cache.Tmdb.Set(key, found, ListLifetime);
        return found;
    }
}
