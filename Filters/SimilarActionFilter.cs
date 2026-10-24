using Gelato.Config;
using Gelato.Services;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

/// <summary>
/// With <see cref="PluginConfiguration.RecommendedRemoteTitles"/> on, a movie's or series' "More
/// like this" leads with TMDB's recommendations for it: the ones the library holds as the
/// library's items, the others as stand-ins that open like search results, at most half the list.
/// The library's own similar items fill the rest.
/// </summary>
/// <remarks>
/// Every major client asks for similar items on every movie and series page, so the request never
/// waits for TMDB: it answers with the recommendations <see cref="TmdbTitleLists"/> keeps, and
/// without them, as Jellyfin does, while they are prepared for the next open. Opening the title's
/// item starts that already, so the page that follows often has them.
/// </remarks>
public sealed class SimilarActionFilter(
    IUserManager userManager,
    ILibraryManager libraryManager,
    GelatoManager manager,
    RemoteTitles remoteTitles,
    TmdbTitleLists lists,
    ILogger<SimilarActionFilter> log
) : IAsyncActionFilter
{
    // Jellyfin's default when the client sends no limit.
    private const int DefaultLimit = 50;

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        if (ctx.GetActionName() is not ("GetItem" or "GetItemLegacy" or "GetSimilarItems") || ctx.GetActionName() is not { } action)
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

        var user = userId == Guid.Empty ? null : userManager.GetUserById(userId);
        if (
            action.Equals("GetItem", StringComparison.OrdinalIgnoreCase)
            || action.Equals("GetItemLegacy", StringComparison.OrdinalIgnoreCase)
        )
        {
            // The title's page asks for its similar items next.
            if (TitleOf(ctx) is { } opened)
                lists.Recommendations(opened, stremio, NeedsRatings(user));
            await next();
            return;
        }

        if (
            !action.Equals("GetSimilarItems", StringComparison.OrdinalIgnoreCase)
            || TitleOf(ctx) is not { } item
            || lists.Recommendations(item, stremio, NeedsRatings(user)) is not { Metas.Count: > 0 } list
        )
        {
            await next();
            return;
        }

        // Beside Jellyfin's own query, not after it.
        var plan = PlanAsync(ctx, cfg, stremio, userId, user, item, list);
        var executed = await next();
        try
        {
            var lead = await plan.ConfigureAwait(false);
            if (
                lead.Count > 0
                && executed.Exception is null
                && executed.Result is ObjectResult { Value: QueryResult<BaseItemDto> local }
            )
            {
                executed.Result = new OkObjectResult(
                    await MergeAsync(ctx, userId, user, item, lead, local).ConfigureAwait(false)
                );
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Similar items of {Name}: TMDB's recommendations failed", item.Name);
        }
    }

    internal static bool NeedsRatings(User? user) =>
        user is not null && ParentalRatings.NeedsRating(user);

    /// <summary>The movie or series a request names, a stream row as its movie.</summary>
    private BaseItem? TitleOf(ActionExecutingContext ctx)
    {
        if (
            !ctx.TryGetActionArgument<Guid>("itemId", out var itemId)
            || libraryManager.GetItemById(itemId) is not { } item
        )
            return null;

        if (
            item.IsStreamRow()
            && (item as Video)?.PrimaryVersionId is { } primaryId
            && libraryManager.GetItemById(primaryId) is { } movie
        )
            item = movie;

        return item is Movie or Series ? item : null;
    }

    private static int Limit(ActionExecutingContext ctx) =>
        ctx.TryGetActionArgument("limit", out int? limit) && limit is { } l ? l : DefaultLimit;

    /// <summary>TMDB's recommendations as the list leads with them (<see cref="RemoteTitles.Lead"/>).</summary>
    private async Task<List<object>> PlanAsync(
        ActionExecutingContext ctx,
        PluginConfiguration cfg,
        GelatoStremioProvider stremio,
        Guid userId,
        User? user,
        BaseItem item,
        TmdbTitleLists.TitleList list
    )
    {
        await Task.Yield();
        var ct = ctx.HttpContext.RequestAborted;
        var limit = Limit(ctx);

        // Where an opened title goes; without a folder for its type there is nowhere to put it,
        // and the library's own titles are all there is.
        var folder =
            item is Series
                ? cfg.SeriesFolder ?? manager.TryGetSeriesFolder(userId)
                : cfg.MovieFolder ?? manager.TryGetMovieFolder(userId);

        var titles = await remoteTitles
            .UsableAsync(list.Metas, cfg, user, folder, stremio, 2 * limit, ct)
            .ConfigureAwait(false);
        var hidden = remoteTitles.HiddenIds(cfg);
        hidden.Add(item.Id);

        return RemoteTitles.Lead(
            titles,
            t => remoteTitles.Matches(list.Held, t),
            hidden,
            user,
            limit,
            folder is null ? 0 : limit / 2,
            []
        );
    }

    private async Task<QueryResult<BaseItemDto>> MergeAsync(
        ActionExecutingContext ctx,
        Guid userId,
        User? user,
        BaseItem item,
        List<object> lead,
        QueryResult<BaseItemDto> local
    )
    {
        ctx.TryGetActionArgument<ItemFields[]>("fields", out var fields, []);
        var standIns = lead.OfType<RemoteTitles.StandIn>().Select(s => (s.Meta, s.Item)).ToList();
        var standInDtos = await remoteTitles
            .BuildStandInsAsync(standIns, userId, ctx.HttpContext.RequestAborted, fields, unheld: true)
            .ConfigureAwait(false);

        var localItems = local.Items ?? [];
        // As Jellyfin's own answer builds them.
        var dtos = remoteTitles.Merge(
            lead,
            localItems,
            new Queue<BaseItemDto>(standInDtos),
            new DtoOptions { Fields = fields },
            user,
            Limit(ctx)
        );

        log.LogDebug(
            "Similar items of {Name}: {Held} held and {StandIns} stand-ins from TMDB, {Local} of the library's {Count}",
            item.Name,
            lead.Count - standIns.Count,
            standIns.Count,
            dtos.Count - lead.Count,
            localItems.Count
        );

        return new QueryResult<BaseItemDto>(0, dtos.Count, dtos);
    }
}
