using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Querying;
using System.Reflection;
using System.Collections;
//using Jellyfin.Api.Models;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ExternalMedia;

public class ExternalMediaResultFilter : IAsyncResultFilter
{
    private readonly ILibraryManager _library;
    private readonly IItemRepository _repo;
    private readonly IMediaSourceManager _mediaSources;
    private readonly IDtoService _dtoService;
    private readonly ExternalMediaStremioProvider _provider;
    private readonly ILogger<ExternalMediaResultFilter> _log;

    public ExternalMediaResultFilter(
        ILibraryManager library,
        IItemRepository repo,
        IMediaSourceManager mediaSources,
        IDtoService dtoService,
        ExternalMediaStremioProvider provider,
        ILogger<ExternalMediaResultFilter> log)
    {
        _library = library;
        _repo = repo;
        _mediaSources = mediaSources;
        _dtoService = dtoService;
        _provider = provider;
        _log = log;
    }

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        var path = context.HttpContext.Request.Path.Value ?? string.Empty;
        if (!path.StartsWith("/Items", StringComparison.OrdinalIgnoreCase))
            if (!IsItemsRoute(path) && !IsUserItemsRoute(path))
            {
                await next(); return;
            }
        _log.LogInformation("ExternalMedia: hooking itno items: {Path}", path);

        if (context.Result is not ObjectResult obj || obj.Value is not BaseItemDto dto || dto.Id == Guid.Empty)
        {
            await next(); return;
        }

        // if (context.Result is not ObjectResult obj)
        // {
        //     await next();
        //     return;
        // }
        // if (context.Result is not ObjectResult obj)
        // {
        //     await next();
        //     return;
        // }

        if (!TryGetBaseItemDtos(obj.Value, out var list))
        {
            //_log.LogInformation("NO LIST");
            await next();
            return;
        }

        // if (!Guid.TryParse(dto.Id, out var id))
        // {
        //     await next(); return;
        // }
        var ct = context.HttpContext.RequestAborted;
        var tasks = list.Select(dto => ProcessDtoAsync(dto, ct)).ToArray();
        await Task.WhenAll(tasks);
        // var meta = await _provider.GetMetaAsync(item);
        // var streams = await _provider.GetStreamsAsync(item);

        // if (meta is not null)
        // {
        //     ApplyMetaToEntity(item, meta!);
        //     _log.LogInformation("ExternalMedia: ({Raring})",  item.CommunityRating);
        //     _repo.SaveItems([item], ct);
        // }

        // if (streams is not null)
        // {
        //     var sourceInfos = streams.Select((s, i) => MapToMediaSourceInfo(dto.Id, i, s)).ToList();

        //     // for (var source in sourceInfos)
        //     // {
        //     //     // Save each media source
        //     //     // _library.CreateItem(item.Id, source, ct);
        //     //     // _mediaSources.Create(item.Id, source, ct);
        //     // }
        // }

        // 3) Rebuild the outgoing DTO from the updated entity
        // var dtoOptions = new DtoOptions(); // customize fields if needed
        // BaseItemDto freshDto;

        // If you have the user id from route/query, pass it, otherwise null
        // freshDto = await _dtoService.GetBaseItemDto(entity, dtoOptions, null, ct);

        // Replace the response payload with the freshly built DTO
        // obj.Value = freshDto;
        _log.LogInformation("ExternalMedia: REACHED");
        await next(); return;
    }

    // private static bool NeedsRefresh(BaseItem item)
    // {
    //     // Make this as strict as you want: timestamps, provider IDs, empty fields, etc.
    //     // Example: refresh if no MediaSources yet or Overview is empty
    //     if (item.MediaSources == null || item.MediaSources.Length == 0) return true;
    //     if (string.IsNullOrWhiteSpace(item.Overview)) return true;
    //     return false;
    // }

    private static bool IsItemsRoute(string path)
        => path.StartsWith("/Items", StringComparison.OrdinalIgnoreCase);

    private static bool IsUserItemsRoute(string path)
    {
        var seg = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return seg.Length >= 3
            && seg[0].Equals("Users", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParse(seg[1], out _)
            && seg[2].Equals("Items", StringComparison.OrdinalIgnoreCase);
    }

    private static bool NeedsRefresh(BaseItemDto item)
    {
        // Make this as strict as you want: timestamps, provider IDs, empty fields, etc.
        // Example: refresh if no MediaSources yet or Overview is empty
        if (item.MediaSources == null || item.MediaSources.Length == 0) return true;
        if (string.IsNullOrWhiteSpace(item.Overview)) return true;
        return false;
    }

    private static void ApplyMetaToEntity(BaseItem entity, StremioMeta meta)
    {
        if (!string.IsNullOrWhiteSpace(meta.Description)) entity.Overview = meta.Description;
        if (!string.IsNullOrWhiteSpace(meta.ImdbRating)) entity.CommunityRating = (float)Convert.ToDouble(meta.ImdbRating);
    }

    private static MediaSourceInfo MapToMediaSourceInfo(Guid itemId, Int64 index, StremioStream s)
    {
        return new MediaSourceInfo
        {
            // Id = $"{itemId}:{(s.Quality ?? "q")}",
            Id = $"{itemId}:{index}",
            Name = s.Quality ?? "External",
            Path = s.Url,
            Protocol = MediaProtocol.Http,
            // Container = s.Container,
            IsRemote = true,
            SupportsDirectPlay = true,
            SupportsTranscoding = true,
            // Fill out MediaStreams if you have track details
            // MediaStreams = new List<MediaStream> { ... }
        };
    }

    private static bool TryGetBaseItemDtos(object value, out IEnumerable<BaseItemDto> list)
    {
        // Plain list/array
        if (value is IEnumerable<BaseItemDto> seq)
        {
            list = seq;
            return true;
        }

        // Jellyfin commonly returns QueryResult<BaseItemDto> with an Items property
        if (value is QueryResult<BaseItemDto> qr && qr.Items is not null)
        {
            list = qr.Items;
            return true;
        }

        // Duck-typing fallback: look for an `Items` property that is IEnumerable<BaseItemDto>
        var prop = value?.GetType().GetProperty("Items", BindingFlags.Public | BindingFlags.Instance);
        if (prop is not null)
        {
            if (prop.GetValue(value) is IEnumerable<BaseItemDto> items)
            {
                list = items;
                return true;
            }
        }

        list = null;
        return false;
    }

    private async Task ProcessDtoAsync(BaseItemDto dto, CancellationToken ct)
    {
        try
        {
            var metaRefresh = dto.ImageTags != null && dto.ImageTags.Count == 0;

            _log.LogInformation("ExternalMedia: path ({P}), {I}, {N}", dto.Path, dto.Id, dto.Name);

            if (!metaRefresh)
                return;

            var item = _library.GetItemById(dto.Id);
            if (item is null)
            {
                _log.LogDebug("ExternalMedia: item {Id} not found in library", dto.Id);
                return;
            }

            _log.LogInformation("ExternalMedia: stremio path ({P})", item.Path);

            var imdb = item.GetProviderId("Imdb");
            if (string.IsNullOrEmpty(imdb))
            {
                _log.LogDebug("ExternalMedia: no IMDb id for {Id}", dto.Id);
                return;
            }

            var meta = await _provider.GetMetaAsync(imdb, "movie");
            if (meta is not null)
            {
                _log.LogInformation("ExternalMedia: applying meta for {Id}", dto.Id);
                ApplyMetaToEntity(item, meta);
                _repo.SaveItems(new[] { item }, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "ExternalMedia: error processing dto {Id}", dto.Id);
        }
    }
}