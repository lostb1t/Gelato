using Gelato.Config;
using Jellyfin.Data.Enums;
using MediaBrowser.Common;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Gelato.Filters;

public class SearchActionFilter(
    GelatoManager manager,
    IApplicationHost appHost,
    ILogger<SearchActionFilter> log
) : IAsyncActionFilter, IOrderedFilter
{
    public int Order => 1;

    public async Task OnActionExecutionAsync(
        ActionExecutingContext ctx,
        ActionExecutionDelegate next
    )
    {
        ctx.TryGetUserId(out var userId);
        var cfg = GelatoPlugin.Instance!.GetConfig(userId);
        if (
            cfg.DisableSearch
            || !ctx.IsApiSearchAction()
            || !ctx.TryGetActionArgument<string>("searchTerm", out var searchTerm)
            || !await cfg.Stremio.IsReady()
        )
        {
            await next();
            return;
        }

        // Strip "local:" prefix if present and pass through to default handler
        if (searchTerm.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
        {
            ctx.ActionArguments["searchTerm"] = searchTerm[6..].Trim();
            await next();
            return;
        }

        // Handle Stremio search
        var requestedTypes = GetRequestedItemTypes(ctx);
        if (requestedTypes.Count == 0)
        {
            await next();
            return;
        }

        ctx.TryGetActionArgument("startIndex", out var start, 0);
        ctx.TryGetActionArgument("limit", out var limit, 25);

        var metas = await SearchMetasAsync(searchTerm, requestedTypes, cfg, userId);
        var pagedMetas = metas.Skip(start).Take(limit).ToList();
        var paged = ConvertMetasToDtos(pagedMetas).ToArray();

        ctx.Result = new OkObjectResult(
            new QueryResult<BaseItemDto> { Items = paged, TotalRecordCount = metas.Count }
        );
    }

    private HashSet<BaseItemKind> GetRequestedItemTypes(ActionExecutingContext ctx)
    {
        var requested = new HashSet<BaseItemKind>([BaseItemKind.Movie, BaseItemKind.Series]);

        // Already parsed as BaseItemKind[] by model binder
        if (
            ctx.TryGetActionArgument<BaseItemKind[]>("includeItemTypes", out var includeTypes)
            && includeTypes is { Length: > 0 }
        )
        {
            requested = new HashSet<BaseItemKind>(includeTypes);
            // Only keep Movie and Series
            requested.IntersectWith([BaseItemKind.Movie, BaseItemKind.Series]);
        }

        // Remove excluded types
        if (
            ctx.TryGetActionArgument<BaseItemKind[]>("excludeItemTypes", out var excludeTypes)
            && excludeTypes is { Length: > 0 }
        )
        {
            requested.ExceptWith(excludeTypes);
        }

        // If mediaTypes=Video, exclude Series
        if (
            ctx.TryGetActionArgument<MediaType[]>("mediaTypes", out var mediaTypes)
            && mediaTypes.Contains(MediaType.Video)
        )
        {
            requested.Remove(BaseItemKind.Series);
        }

        return requested;
    }

    private async Task<List<StremioMeta>> SearchMetasAsync(
        string searchTerm,
        HashSet<BaseItemKind> requestedTypes,
        PluginConfiguration cfg,
        Guid userId
    )
    {
        var tasks = new List<Task<IReadOnlyList<StremioMeta>>>();
        var movieFolder = cfg.MovieFolder ?? manager.TryGetMovieFolder(userId);
        var seriesFolder = cfg.SeriesFolder ?? manager.TryGetSeriesFolder(userId);

        // Keep hot config in sync for subsequent searches in this request window.
        cfg.MovieFolder = movieFolder;
        cfg.SeriesFolder = seriesFolder;

        if (requestedTypes.Contains(BaseItemKind.Movie) && movieFolder is not null)
        {
            tasks.Add(cfg.Stremio.SearchAsync(searchTerm, StremioMediaType.Movie));
        }
        else if (requestedTypes.Contains(BaseItemKind.Movie))
        {
            log.LogWarning(
                "No movie folder found, please add your gelato path to a library and rescan. skipping search"
            );
        }

        if (requestedTypes.Contains(BaseItemKind.Series) && seriesFolder is not null)
        {
            tasks.Add(cfg.Stremio.SearchAsync(searchTerm, StremioMediaType.Series));
        }
        else if (requestedTypes.Contains(BaseItemKind.Series))
        {
            log.LogWarning(
                "No series folder found, please add your gelato path to a library and rescan. skipping search"
            );
        }

        var results = (await Task.WhenAll(tasks)).SelectMany(r => r).ToList();

        var filterUnreleased = cfg.FilterUnreleased;
        var bufferDays = cfg.FilterUnreleasedBufferDays;

        if (filterUnreleased)
        {
            results = results.Where(x => x.IsReleased(bufferDays)).ToList();
        }

        return results;
    }

    private List<BaseItemDto> ConvertMetasToDtos(List<StremioMeta> metas)
    {
        var dtos = new List<BaseItemDto>(metas.Count);

        foreach (var meta in metas)
        {
            var dto = ConvertMetaToSearchDto(meta, appHost.SystemId);
            if (dto is null)
                continue;

            dtos.Add(dto);
            manager.SaveStremioMeta(dto.Id, meta);
        }

        return dtos;
    }

    private static BaseItemDto? ConvertMetaToSearchDto(StremioMeta meta, string serverId)
    {
        var itemKind = meta.Type switch
        {
            StremioMediaType.Movie => BaseItemKind.Movie,
            StremioMediaType.Series => BaseItemKind.Series,
            _ => (BaseItemKind?)null,
        };

        if (itemKind is null)
            return null;

        var externalId = meta.ImdbId ?? meta.Id;
        if (string.IsNullOrWhiteSpace(externalId))
            return null;

        var providerIds = meta.GetProviderIds();
        providerIds["Stremio"] = externalId;

        var stremioUri = new StremioUri(meta.Type, externalId);
        var primaryImage = meta.Poster ?? meta.Thumbnail;
        var name = meta.GetName();

        return new BaseItemDto
        {
            ServerId = serverId,
            Id = stremioUri.ToGuid(),
            Name = name,
            SortName = name,
            Type = itemKind.Value,
            MediaType = MediaType.Video,
            VideoType = VideoType.VideoFile,
            LocationType = LocationType.Remote,
            Path = $"gelato://stub/{meta.Id}",
            CanDownload = true,
            IsFolder = meta.Type == StremioMediaType.Series,
            Overview = meta.Description ?? meta.Overview,
            PremiereDate = meta.GetPremiereDate(),
            ProductionYear = meta.GetYear(),
            RunTimeTicks = Utils.ParseToTicks(meta.Runtime),
            ProviderIds = providerIds,
            Genres = (meta.Genres ?? meta.Genre)?.ToArray(),
            ImageTags = string.IsNullOrWhiteSpace(primaryImage)
                ? null
                : new Dictionary<ImageType, string> { [ImageType.Primary] = "stremio" },
            BackdropImageTags = string.IsNullOrWhiteSpace(meta.Background) ? null : ["stremio"],
        };
    }
}
