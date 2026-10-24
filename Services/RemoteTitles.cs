using System.Globalization;
using Gelato.Config;
using Gelato.Decorators;
using Gelato.Filters;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging;

namespace Gelato.Services;

/// <summary>
/// Titles from TMDB that a listing shows next to the library's own, as stand-ins that open like
/// search results: a person's films and series (<see cref="PersonActionFilter"/>) and a title's
/// recommendations (<see cref="SimilarActionFilter"/>, <see cref="SuggestionsActionFilter"/>).
/// What they share: the meta a TMDB title stands for, the lookups that make it usable, which of
/// them the library holds, and the DTOs.
/// </summary>
public sealed class RemoteTitles(
    IDtoService dtoService,
    IServerConfigurationManager serverConfig,
    ILibraryManager libraryManager,
    IItemRepository itemRepository,
    GelatoManager manager,
    SearchActionFilter search,
    ParentalRatings parental,
    ILogger<RemoteTitles> log
)
{
    /// <summary>A TMDB title ready to be listed: its meta and the item it stands for.</summary>
    public sealed record StandIn(StremioMeta Meta, BaseItem Item);

    /// <summary>How long a page waits for TMDB release lookups and imdb ids of its stand-ins.</summary>
    public static readonly TimeSpan LookupBudget = TimeSpan.FromSeconds(3);

    /// <summary>The meta a TMDB title stands for, keyed by its TMDB id as the addon resolves it.</summary>
    public static StremioMeta? ToMeta(TmdbTitle title)
    {
        var type = title.MediaType switch
        {
            "movie" => StremioMediaType.Movie,
            "tv" => StremioMediaType.Series,
            _ => StremioMediaType.Unknown,
        };
        var name = type == StremioMediaType.Movie ? title.Title : title.Name;
        if (type == StremioMediaType.Unknown || string.IsNullOrWhiteSpace(name))
            return null;

        var date = type == StremioMediaType.Movie ? title.ReleaseDate : title.FirstAirDate;
        DateTime? released = DateTime.TryParseExact(
            date,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var d
        )
            ? d
            : null;

        return new StremioMeta
        {
            Id = $"tmdb:{title.Id.ToString(CultureInfo.InvariantCulture)}",
            Type = type,
            Name = name,
            Description = title.Overview,
            Poster = title.PosterPath is { Length: > 0 } poster
                ? $"https://image.tmdb.org/t/p/w500{poster}"
                : null,
            Background = title.BackdropPath is { Length: > 0 } backdrop
                ? $"https://image.tmdb.org/t/p/original{backdrop}"
                : null,
            Released = released,
            Year = released?.Year,
            ImdbRating = title.VoteCount > 0 ? title.VoteAverage : null,
        };
    }

    /// <summary>
    /// The server's metadata language, as TMDB takes it ("de-DE"): the titles the library's own
    /// items are named in.
    /// </summary>
    public string? MetadataLanguage()
    {
        var language = serverConfig.Configuration.PreferredMetadataLanguage;
        var country = serverConfig.Configuration.MetadataCountryCode;
        if (string.IsNullOrWhiteSpace(language))
            return null;
        return string.IsNullOrWhiteSpace(country) || language.Contains('-')
            ? language
            : $"{language}-{country}";
    }

    /// <summary>
    /// As search does for the unreleased filter: a movie premiered within the last year gets
    /// TMDB's digital release date, memoised per movie. Within <paramref name="budget"/>; one
    /// that does not answer in time keeps none.
    /// </summary>
    public async Task EnrichReleaseDatesAsync(
        IEnumerable<StremioMeta> metas,
        GelatoStremioProvider stremio,
        TimeSpan budget,
        CancellationToken ct
    )
    {
        var lookups = metas.Where(m => m.NeedsReleaseLookup()).ToList();
        if (lookups.Count == 0)
            return;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);
        try
        {
            await Parallel
                .ForEachAsync(
                    lookups,
                    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cts.Token },
                    async (m, t) =>
                        await stremio.EnrichDigitalReleaseDateAsync(m, t).ConfigureAwait(false)
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogDebug("TMDB release lookups ran out of time");
        }
    }

    /// <summary>
    /// A meta addon that takes no tmdb: ids (Cinemeta) cannot open a title by its TMDB id: such a
    /// stand-in carries its imdb id, as search's results do. Sets it, within
    /// <paramref name="budget"/>, and answers the metas it was set on.
    /// </summary>
    public async Task<HashSet<StremioMeta>> ApplyImdbIdsAsync(
        IEnumerable<StremioMeta> metas,
        GelatoStremioProvider stremio,
        TimeSpan budget,
        CancellationToken ct
    )
    {
        var list = metas.ToList();
        var changed = new HashSet<StremioMeta>(ReferenceEqualityComparer.Instance);
        var servesTmdb = new Dictionary<StremioMediaType, bool>();
        foreach (var type in list.Select(m => m.Type).Distinct())
            servesTmdb[type] = await stremio.ServesTmdbMetaAsync(type).ConfigureAwait(false);

        var lookups = list.Where(m => !servesTmdb[m.Type] && string.IsNullOrEmpty(m.ImdbId)).ToList();
        if (lookups.Count == 0)
            return changed;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);
        try
        {
            await Parallel
                .ForEachAsync(
                    lookups,
                    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cts.Token },
                    async (m, t) =>
                    {
                        var tmdbId = m.Id["tmdb:".Length..];
                        if (
                            await stremio
                                .GetCachedImdbIdFromTmdbAsync(tmdbId, m.Type, t)
                                .ConfigureAwait(false)
                            is not { } imdbId
                        )
                            return;

                        m.ImdbId = imdbId;
                        lock (changed)
                            changed.Add(m);
                    }
                )
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogDebug("TMDB imdb id lookups ran out of time");
        }

        return changed;
    }

    /// <summary>
    /// The stand-ins' DTOs, in their order, as search builds its results, each kept under its
    /// search id so that opening it puts the title into the library. With
    /// <paramref name="fields"/> only those of search's fields: a list's cards need no more.
    /// <paramref name="unheld"/>: the caller knows the library holds none of them (its list
    /// found which it does), so the database is not asked which ids it knows.
    /// </summary>
    public async Task<List<BaseItemDto>> BuildStandInsAsync(
        IReadOnlyList<(StremioMeta Meta, BaseItem Item)> titles,
        Guid userId,
        CancellationToken ct,
        ItemFields[]? fields = null,
        bool unheld = false
    )
    {
        if (titles.Count == 0)
            return [];

        var options = new DtoOptions(false)
        {
            Fields = fields is null
                ? SearchActionFilter.SearchResultFields
                : [.. SearchActionFilter.SearchResultFields.Intersect(fields)],
            EnableImages = true,
            EnableUserData = false,
        };
        var unknownOptions = new DtoOptions(false)
        {
            Fields = fields is null
                ? SearchActionFilter.UnknownItemFields
                : [.. SearchActionFilter.UnknownItemFields.Intersect(fields)],
            EnableImages = true,
            EnableUserData = false,
        };
        var unknownIds = unheld
            ? titles.Select(t => t.Item.Id).ToHashSet()
            : await search
                .FindUnknownIdsAsync(titles.Select(t => t.Item.Id), ct)
                .ConfigureAwait(false);

        var dtos = new List<BaseItemDto>(titles.Count);
        foreach (var (meta, item) in titles)
        {
            var searchId = StremioUri.FromBaseItem(item)!.ToGuid();
            var dto = unknownIds.Contains(item.Id)
                ? search.GetUnknownItemDto(item, unknownOptions)
                : dtoService.GetBaseItemDto(item, options);
            dto.Id = searchId;
            dtos.Add(dto);

            manager.SaveStremioMeta(searchId, meta);
            manager.RememberSearchFolder(userId, searchId, null);
            manager.RememberSearchCopy(userId, searchId, false);
        }

        return dtos;
    }

    /// <summary>
    /// The titles of <paramref name="metas"/> this user may be shown, without asking TMDB: with
    /// the unreleased filter on, the released ones by the dates kept with them; under parental
    /// control, as search judges them, one whose rating is not known yet left out. The first
    /// <paramref name="max"/> of them: a list shows no more, and each costs a lookup.
    /// </summary>
    public async Task<List<StandIn>> UsableAsync(
        IReadOnlyList<StremioMeta> metas,
        PluginConfiguration cfg,
        User? user,
        Folder? folder,
        GelatoStremioProvider stremio,
        int max,
        CancellationToken ct
    )
    {
        var kept = metas.ToList();
        if (cfg.FilterUnreleased)
            kept = kept.Where(m => m.IsReleased(cfg.FilterUnreleasedBufferDays)).ToList();

        if (ParentalRatings.Restricts(user))
        {
            kept = await parental
                .FilterAsync(kept, user, _ => folder, stremio, ct, TimeSpan.Zero)
                .ConfigureAwait(false);
        }

        return kept.Take(max)
            .Select(m => (Meta: m, Item: manager.IntoBaseItem(m)))
            .Where(t => t.Item is not null)
            .Select(t => new StandIn(t.Meta, t.Item!))
            .ToList();
    }

    /// <summary>
    /// What the library's listings hide, so no recommendation either: the titles opened from
    /// search and never played, and with the unreleased filter on, the unreleased ones.
    /// </summary>
    public HashSet<Guid> HiddenIds(PluginConfiguration cfg)
    {
        var hidden = new HashSet<Guid>();
        if (itemRepository is GelatoItemRepository repository)
        {
            if (GelatoPlugin.Instance?.Configuration.HideUnplayedSearchTitles == true)
                hidden.UnionWith(repository.GetPendingIds());
            if (cfg.FilterUnreleased)
                hidden.UnionWith(repository.GetUnreleasedIds(cfg.FilterUnreleasedBufferDays));
        }

        return hidden;
    }

    /// <summary>
    /// The library's items of a title: as its list found them (<see cref="TmdbTitleLists"/>), or,
    /// for a stand-in opened a moment ago, by the id it was opened as (its TMDB id only reaches the
    /// item with the refresh after the insert).
    /// </summary>
    public IReadOnlyList<BaseItem> Matches(
        IReadOnlyDictionary<string, Guid[]> held,
        StandIn title
    )
    {
        var matches = held.TryGetValue(title.Meta.Id, out var ids)
            ? ids.Select(id => libraryManager.GetItemById(id))
                .Where(i => i is not null)
                .Select(i => i!)
                .ToList()
            : [];
        if (
            matches.Count == 0
            && manager.GetInsertedId(StremioUri.FromBaseItem(title.Item)!.ToGuid()) is { } opened
            && libraryManager.GetItemById(opened) is { } openedItem
        )
            return [openedItem];
        return matches;
    }

    /// <summary>
    /// TMDB's titles as a list leads with them, in their order, up to <paramref name="limit"/>: a
    /// title the library holds as its item, where this user sees it and listings show it, one it
    /// does not hold as a <see cref="StandIn"/>, at most <paramref name="standInLimit"/>.
    /// <paramref name="seen"/> takes what is listed, and keeps out what already is.
    /// </summary>
    public static List<object> Lead(
        IEnumerable<StandIn> titles,
        Func<StandIn, IReadOnlyList<BaseItem>> matchesOf,
        HashSet<Guid> hidden,
        User? user,
        int limit,
        int standInLimit,
        HashSet<Guid> seen
    )
    {
        var lead = new List<object>();
        var standIns = 0;
        foreach (var title in titles)
        {
            if (lead.Count >= limit)
                break;

            var matches = matchesOf(title);
            if (matches.Count == 0)
            {
                if (standIns < standInLimit && seen.Add(title.Item.Id))
                {
                    standIns++;
                    lead.Add(title);
                }
                continue;
            }

            if (
                matches.FirstOrDefault(m =>
                    !hidden.Contains(m.Id) && (user is null || m.IsVisibleStandalone(user))
                )
                    is { } visible
                && seen.Add(visible.Id)
            )
                lead.Add(visible);
        }

        return lead;
    }

    /// <summary>
    /// The DTOs of <paramref name="lead"/>, then those of <paramref name="local"/>, each once, up
    /// to <paramref name="limit"/>. A library item that <paramref name="local"/> has keeps the DTO
    /// Jellyfin built; a stand-in takes the next of <paramref name="standInDtos"/>
    /// (<see cref="BuildStandInsAsync"/>, in the same order).
    /// </summary>
    public List<BaseItemDto> Merge(
        IReadOnlyList<object> lead,
        IReadOnlyCollection<BaseItemDto> local,
        Queue<BaseItemDto> standInDtos,
        DtoOptions options,
        User? user,
        int limit
    )
    {
        var localById = new Dictionary<Guid, BaseItemDto>();
        foreach (var dto in local)
            localById.TryAdd(dto.Id, dto);

        var dtos = new List<BaseItemDto>(limit);
        var seen = new HashSet<Guid>();
        foreach (var entry in lead)
        {
            if (entry is BaseItem own)
            {
                seen.Add(own.Id);
                dtos.Add(
                    localById.TryGetValue(own.Id, out var built)
                        ? built
                        : dtoService.GetBaseItemDto(own, options, user)
                );
            }
            else
            {
                dtos.Add(standInDtos.Dequeue());
            }
        }

        foreach (var dto in local)
        {
            if (dtos.Count >= limit)
                break;
            if (seen.Add(dto.Id))
                dtos.Add(dto);
        }

        return dtos;
    }
}
