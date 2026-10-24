using Gelato.Config;
using Gelato.Services;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

/// <summary>
/// With <see cref="PluginConfiguration.RecommendedRemoteTitles"/> on, the rows of the Movies
/// Suggestions tab (<c>/Movies/Recommendations</c>) add TMDB's titles the library does not hold,
/// as stand-ins that open like search results, at most half of each row so that the library's own
/// titles stay. "Because you watched" and "Because you liked" lead with TMDB's recommendations for
/// the movie, as "More like this" does (<see cref="SimilarActionFilter"/>); "Starring" and
/// "Directed by" keep the library's titles and add the person's other films from TMDB after them.
/// </summary>
/// <remarks>
/// Jellyfin picks a row's movie or person at random per request, so nothing of the answer can be
/// kept, only TMDB's side (<see cref="TmdbTitleLists"/>), and the request never waits for it: a row
/// whose list is not kept yet is Jellyfin's, and the list is prepared for the next time.
/// </remarks>
public sealed class SuggestionsActionFilter(
    IUserManager userManager,
    ILibraryManager libraryManager,
    GelatoManager manager,
    RemoteTitles remoteTitles,
    TmdbTitleLists lists,
    ILogger<SuggestionsActionFilter> log
) : IAsyncActionFilter
{
    // Jellyfin's default when the client sends no item limit.
    private const int DefaultItemLimit = 8;

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        if (ctx.GetActionName() != "GetMovieRecommendations")
        {
            await next();
            return;
        }

        ctx.TryGetUserId(out var userId);
        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        if (!cfg.RecommendedRemoteTitles || cfg.DisableSearch || cfg.Stremio is not { } stremio)
        {
            await next();
            return;
        }

        var executed = await next();
        if (
            executed.Exception is not null
            || executed.Result is not ObjectResult { Value: IEnumerable<RecommendationDto> answer }
        )
            return;

        try
        {
            var rows = answer.ToList();
            if (await AddAsync(ctx, cfg, stremio, userId, rows).ConfigureAwait(false))
                executed.Result = new OkObjectResult(rows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Movie suggestions: TMDB's titles failed");
        }
    }

    private sealed record Row(
        RecommendationDto Dto,
        List<RemoteTitles.StandIn> Titles,
        IReadOnlyDictionary<string, Guid[]> Held,
        bool Similar
    );

    /// <summary>Adds TMDB's titles to the rows it keeps a list for; whether any row changed.</summary>
    private async Task<bool> AddAsync(
        ActionExecutingContext ctx,
        PluginConfiguration cfg,
        GelatoStremioProvider stremio,
        Guid userId,
        List<RecommendationDto> rows
    )
    {
        var ct = ctx.HttpContext.RequestAborted;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var user = userId == Guid.Empty ? null : userManager.GetUserById(userId);
        var withRatings = SimilarActionFilter.NeedsRatings(user);
        var limit =
            ctx.TryGetActionArgument("itemLimit", out int? itemLimit) && itemLimit is { } l
                ? l
                : DefaultItemLimit;
        var folder = cfg.MovieFolder ?? manager.TryGetMovieFolder(userId);

        var lookups = new List<Row>();
        foreach (var dto in rows)
        {
            var similar = dto.RecommendationType
                is RecommendationType.SimilarToRecentlyPlayed
                    or RecommendationType.SimilarToLikedItem;
            var list = dto.RecommendationType switch
            {
                _ when similar => libraryManager.GetItemById(dto.CategoryId) is Movie baseline
                    ? lists.Recommendations(baseline, stremio, withRatings)
                    : null,
                RecommendationType.HasDirectorFromRecentlyPlayed
                or RecommendationType.HasLikedDirector => libraryManager.GetPerson(
                    dto.BaselineItemName
                )
                    is { } director
                    ? lists.PersonMovies(director, true, stremio, withRatings)
                    : null,
                RecommendationType.HasActorFromRecentlyPlayed
                or RecommendationType.HasLikedActor => libraryManager.GetPerson(
                    dto.BaselineItemName
                )
                    is { } actor
                    ? lists.PersonMovies(actor, false, stremio, withRatings)
                    : null,
                _ => null,
            };
            if (list is not { Metas.Count: > 0 })
                continue;

            var titles = await remoteTitles
                .UsableAsync(list.Metas, cfg, user, folder, stremio, 2 * limit, ct)
                .ConfigureAwait(false);
            lookups.Add(new Row(dto, titles, list.Held, similar));
        }

        if (lookups.Count == 0)
            return false;

        var hidden = remoteTitles.HiddenIds(cfg);
        var standInLimit = folder is null ? 0 : limit / 2;

        var leads = new List<(Row Row, List<object> Lead)>();
        foreach (var row in lookups)
        {
            var local = row.Dto.Items ?? [];
            var seen = new HashSet<Guid>();
            List<object> lead;
            if (row.Similar)
            {
                seen.Add(row.Dto.CategoryId);
                lead = RemoteTitles.Lead(
                    row.Titles,
                    t => remoteTitles.Matches(row.Held, t),
                    [.. hidden, row.Dto.CategoryId],
                    user,
                    limit,
                    standInLimit,
                    seen
                );
            }
            else
            {
                // The person's films the library does not hold, after the library's own: a held
                // one Jellyfin left out (played, or not the person's in the library) stays out.
                seen.UnionWith(local.Select(d => d.Id));
                var room = Math.Min(standInLimit, limit - Math.Min(local.Count, limit));
                lead =
                [
                    .. row
                        .Titles.Where(t => remoteTitles.Matches(row.Held, t).Count == 0)
                        .Where(t => seen.Add(t.Item.Id))
                        .Take(room),
                ];
            }

            if (lead.Count > 0)
                leads.Add((row, lead));
        }

        if (leads.Count == 0)
            return false;

        ctx.TryGetActionArgument<ItemFields[]>("fields", out var fields, []);
        var standIns = leads
            .SelectMany(l => l.Lead.OfType<RemoteTitles.StandIn>())
            .Select(s => (s.Meta, s.Item))
            .ToList();
        var standInDtos = new Queue<BaseItemDto>(
            await remoteTitles.BuildStandInsAsync(standIns, userId, ct, fields, unheld: true).ConfigureAwait(false)
        );
        // As Jellyfin's own answer builds them.
        var options = new DtoOptions { Fields = fields };

        foreach (var (row, lead) in leads)
        {
            var local = row.Dto.Items ?? [];
            row.Dto.Items = row.Similar
                ? remoteTitles.Merge(lead, local, standInDtos, options, user, limit)
                : [.. local.Take(limit), .. lead.Select(_ => standInDtos.Dequeue())];
        }

        log.LogDebug(
            "Movie suggestions: {StandIns} stand-ins from TMDB in {Rows} of {Count} rows, {Ms:F1} ms",
            standIns.Count,
            leads.Count,
            rows.Count,
            System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds
        );
        return true;
    }
}
