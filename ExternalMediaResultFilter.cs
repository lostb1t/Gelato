using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;

using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;

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
        if (!IsItemsRoute(path) && !IsUserItemsRoute(path))
        {
            await next();
            return;
        }

        if (context.Result is not ObjectResult obj || obj.Value is not BaseItemDto dto || dto.Id == Guid.Empty)
        {
            await next();
            return;
        }

        var entity = _library.GetItemById(dto.Id);
        if (entity is not Video primary)
        {
            await next();
            return;
        }

        var ct = context.HttpContext.RequestAborted;

        var streams = await _provider.GetStreamsAsync(entity).ConfigureAwait(false);
        if (streams is not null)
        {
            await ReplaceAlternatesAsync(primary, streams, ct).ConfigureAwait(false);
        }

        var dtoOptions = new DtoOptions
        {
            Fields = new[] { ItemFields.MediaSources } // include sources in response
        };
        obj.Value = _dtoService.GetBaseItemDto(entity, dtoOptions);

        await next();
    }

    /// <summary>
    /// Delete all current alternates, then rebuild from streams.
    /// </summary>
    private async Task ReplaceAlternatesAsync(Video primary, IEnumerable<StremioStream> streams, CancellationToken ct)
    {
        var parent = primary.GetParent() as Folder ?? (_library.GetItemById(primary.ParentId) as Folder);
        if (parent is null)
        {
            _log.LogWarning("ExternalMedia: primary {Id} has no parent; cannot replace alternates", primary.Id);
            return;
        }

        // 1) Delete all currently linked alternates
        var existing = primary.LinkedAlternateVersions ?? Array.Empty<LinkedChild>();
        foreach (var link in existing)
        {
            if (link.ItemId is Guid id)
            {
                try
                {
                    _repo.DeleteItem(id);
                    _log.LogInformation("ExternalMedia: deleted old alternate {Alt} for {Primary}", id, primary.Id);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "ExternalMedia: failed to delete alternate {Alt}", id);
                }
            }
        
        }
        primary.LinkedAlternateVersions = Array.Empty<LinkedChild>();
       // _repo.SaveItems(new BaseItem[] { primary }, ct);

        // 2) Build & persist fresh alternates
        var newLinks = new List<LinkedChild>();
          //          var parent = _library.GetParentItem(primary);
        foreach (var s in streams)
        {
            if (s is null || string.IsNullOrWhiteSpace(s.Url))
                continue;

            var alt = BuildAlternate(primary, s);
            if (alt is null)
                continue;

            alt.PresentationUniqueKey = alt.CreatePresentationUniqueKey();
            

            parent.AddChild(alt);

            //_repo.SaveItems(new BaseItem[] { alt }, ct);

            newLinks.Add(new LinkedChild { ItemId = alt.Id, Path = alt.Path });
            _log.LogInformation("ExternalMedia: inserted alternate {Alt} -> {Url}", alt.Id, s.Url);
        }

        // 3) Link them on the primary
        primary.LinkedAlternateVersions = newLinks.ToArray();
      await primary.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
 // _repo.SaveItems(new BaseItem[] { primary }, ct);
    }

    /// <summary>
    /// Build a concrete alternate (Movie/Episode) matching the primary's concrete type.
    /// </summary>
    private Video? BuildAlternate(Video primary, StremioStream s)
    {
        Video alt;

        if (primary is Movie)
        {
            alt = new Movie
            {
                Id = _library.GetNewItemId(s.Url, typeof(Movie)),
                Name = string.IsNullOrWhiteSpace(s.Name) ? primary.Name : s.Name,
                Path = s.Url,
                IsVirtualItem = true,
                PrimaryVersionId = primary.Id.ToString("N")
            };
        }
        else if (primary is Episode epPrimary)
        {
            alt = new Episode
            {
                Id = _library.GetNewItemId(s.Url, typeof(Episode)),
                Name = string.IsNullOrWhiteSpace(s.Name) ? primary.Name : s.Name,
                Path = s.Url,
                IsVirtualItem = true,
                PrimaryVersionId = primary.Id.ToString("N"),

                // Keep episode context so UI/queries behave
                IndexNumber = epPrimary.IndexNumber,
                ParentIndexNumber = epPrimary.ParentIndexNumber,
                SeriesName = epPrimary.SeriesName,
                SeriesId = epPrimary.SeriesId,
                SeasonId = epPrimary.SeasonId
            };
        }
        else
        {
            return null; // not handling Series/Season/Folder as "versioned" primaries
        }

        // Copy provider ids (helps UI & queries)
        CopyProviderIds(primary, alt);
        return alt;
    }

    private static void CopyProviderIds(BaseItem from, BaseItem to)
    {
        foreach (var kv in from.ProviderIds)
            to.SetProviderId(kv.Key, kv.Value);
    }

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

    // Optional helpers kept in case you handle list shapes later
    private static bool TryGetBaseItemDtos(object value, out IEnumerable<BaseItemDto> list)
    {
        if (value is IEnumerable<BaseItemDto> seq)
        {
            list = seq;
            return true;
        }

        if (value is QueryResult<BaseItemDto> qr && qr.Items is not null)
        {
            list = qr.Items;
            return true;
        }

        var prop = value?.GetType().GetProperty("Items", BindingFlags.Public | BindingFlags.Instance);
        if (prop is not null && prop.GetValue(value) is IEnumerable<BaseItemDto> items)
        {
            list = items;
            return true;
        }

        list = null!;
        return false;
    }

    public static string MakeStreamId(string url)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
        return BitConverter.ToString(hash, 0, 12).Replace("-", "").ToLowerInvariant();
    }

    
}