using System;
using System.Collections.Generic;
using System.Linq;
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
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Jellyfin.Plugin.ExternalMedia.Common;

namespace Jellyfin.Plugin.ExternalMedia;

public class ExternalMediaInsertActionFilter : IAsyncActionFilter
{
    private readonly ILibraryManager _library;
    private readonly IItemRepository _repo;
    private readonly IMediaSourceManager _mediaSources;
    private readonly IDtoService _dtoService;
    private readonly ExternalMediaStremioProvider _provider;
    private readonly ILogger<ExternalMediaInsertActionFilter> _log;

    //public int Order { get; set; } = 1;

    public ExternalMediaInsertActionFilter(
        ILibraryManager library,
        IItemRepository repo,
        IMediaSourceManager mediaSources,
        IDtoService dtoService,
        ExternalMediaStremioProvider provider,
        ILogger<ExternalMediaInsertActionFilter> log)
    {
        _library = library;
        _repo = repo;
        _mediaSources = mediaSources;
        _dtoService = dtoService;
        _provider = provider;
        _log = log;
    }


    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {

        _log.LogInformation("ExternalMedia: ExternalMediaInsertActionFilter");
        if (!TryGetRouteGuid(context, out var guid))
        {
            // _log.LogInformation("ExternalMedia: NO ROUTE");
            await next();
            return;
        }

        var item = _library.GetItemById(guid);
        if (item is not null)
        {
            await next();
            return;
        }

        var stremioId = StremioId.Decode(GuidCodec.DecodeString(guid));
        // _log.LogInformation("ExternalMedia: GOstremioIdING {Guid}", stremioId);
        var parsed = StremioId.Parse(stremioId);
        if (parsed is null)
        {
            await next();
            return;
        }

        _log.LogInformation("ExternalMedia: GOING {Guid}", guid);

        var (mediaType, imdbId) = parsed.Value;

        item = FindByImdb(imdbId) as Video;

        var cfg = ExternalMediaPlugin.Instance!.Configuration;

        if (item is null)
        {
            var meta = await _provider.GetMetaAsync(imdbId, mediaType).ConfigureAwait(false);
            if (meta is not null)
            {
                _log.LogInformation("ExternalMedia: ID {Guid}", meta.Id);
                // var item = _provider.IntoBaseItem(meta);
                // _library.CreateItem(item, parentCollection);
                // parentCollection.AddChild(item);

                // var streams = await _provider.GetStreamsAsync(entity).ConfigureAwait(false);
                // var built = _provider.IntoBaseItem(meta);
                //  built.Id = guid; // match the route guid
                // if (!string.IsNullOrWhiteSpace(meta.Imdb_Id))
                //    built.SetProviderId(MetadataProvider.Imdb, meta.Imdb_Id);

                //item = built as Video;
            }
        }

        _log.LogInformation("ExternalMedia: end of the road");

        await next();
        return;
    }



    private bool TryGetRouteGuid(ActionExecutingContext ctx, out Guid value)
    {
        value = Guid.Empty;


        var rd = ctx.RouteData.Values;
        foreach (var _key in new[] { "id", "Id", "ID", "itemId", "ItemId", "ItemID" })
        {
            if (rd.TryGetValue(_key, out var raw) && raw is not null)
            {
                //  _log.LogInformation("ExternalMedia: RAW {Guid}", raw);
                var s = raw.ToString();
                _log.LogDebug("ExternalMedia: route[{Key}] = {Val}", _key, s);
                if (!string.IsNullOrWhiteSpace(s) && Guid.TryParse(s, out value))
                    return true;
            }
        }

        return false;

    }

    public BaseItem? FindByImdb(string imdbId)
    {
        if (string.IsNullOrWhiteSpace(imdbId)) return null;

        var q = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            Limit = 1,
            HasAnyProviderId = new Dictionary<string, string> { { MetadataProvider.Imdb.ToString(), imdbId } }
            // ImdbId = imdbId,
            // ProviderIds = new Dictionary<string,string>
            // {
            //    [MetadataProvider.Imdb.ToString()] = imdbId
            //}
        };

        return _library.GetItemList(q).FirstOrDefault();
    }

    private async Task ReplaceAlternatesAsync(Video primary, IEnumerable<StremioStream> streams, CancellationToken ct)
    {
        var parent = primary.GetParent() as Folder ?? (_library.GetItemById(primary.ParentId) as Folder);
        if (parent is null)
        {
            _log.LogWarning("ExternalMedia: primary {Id} has no parent; cannot replace alternates", primary.Id);
            return;
        }

        var existing = primary.LinkedAlternateVersions ?? Array.Empty<LinkedChild>();
        foreach (var link in existing)
        {
            if (link.ItemId is Guid id)
            {
                try { _repo.DeleteItem(id); }
                catch (Exception ex) { _log.LogWarning(ex, "ExternalMedia: failed to delete alternate {Alt}", id); }
            }
        }
        primary.LinkedAlternateVersions = Array.Empty<LinkedChild>();

        var newLinks = new List<LinkedChild>();
        foreach (var s in streams)
        {
            if (s is null || string.IsNullOrWhiteSpace(s.Url)) continue;

            var alt = BuildAlternate(primary, s);
            if (alt is null) continue;

            alt.PresentationUniqueKey = alt.CreatePresentationUniqueKey();
            parent.AddChild(alt);

            newLinks.Add(new LinkedChild { ItemId = alt.Id, Path = alt.Path });
        }

        primary.LinkedAlternateVersions = newLinks.ToArray();
        await primary.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
    }

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
                IndexNumber = epPrimary.IndexNumber,
                ParentIndexNumber = epPrimary.ParentIndexNumber,
                SeriesName = epPrimary.SeriesName,
                SeriesId = epPrimary.SeriesId,
                SeasonId = epPrimary.SeasonId
            };
        }
        else
        {
            return null;
        }

        CopyProviderIds(primary, alt);
        return alt;
    }

    private static void CopyProviderIds(BaseItem from, BaseItem to)
    {
        foreach (var kv in from.ProviderIds)
            to.SetProviderId(kv.Key, kv.Value);
    }

    // public static string MakeStreamId(string url)
    // {
    //     using var sha = SHA256.Create();
    //     var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(url));
    //     return BitConverter.ToString(hash, 0, 12).Replace("-", "").ToLowerInvariant();
    // }
}