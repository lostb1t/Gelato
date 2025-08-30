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
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;

namespace Jellyfin.Plugin.ExternalMedia;

public class ExternalMediaInsertActionFilter : IAsyncResourceFilter
{
    private readonly ILibraryManager _library;
    private readonly IItemRepository _repo;
    private readonly IMediaSourceManager _mediaSources;
    private readonly IDtoService _dtoService;
    private readonly ExternalMediaStremioProvider _stremioProvider;
    private readonly ILogger<ExternalMediaInsertActionFilter> _log;
    private readonly ExternalMediaManager _manager;
    private readonly IProviderManager _provider;
    private readonly IFileSystem _fileSystem;

    //public int Order { get; set; } = 1;
    private readonly ILibraryMonitor _libraryMonitor;
    public ExternalMediaInsertActionFilter(
        ILibraryManager library,
        IFileSystem fileSystem,
        IItemRepository repo,
        IMediaSourceManager mediaSources,
          ExternalMediaManager manager,
        IDtoService dtoService,
        ExternalMediaStremioProvider stremioProvider,
            IProviderManager provider,
            ILibraryMonitor libraryMonitor,
        ILogger<ExternalMediaInsertActionFilter> log)
    {
        _library = library;
        _repo = repo;
        _mediaSources = mediaSources;
        _dtoService = dtoService;
        _provider = provider;
        _stremioProvider = stremioProvider;
        _fileSystem = fileSystem;
        _manager = manager;
        _log = log;
        _libraryMonitor = libraryMonitor;
    }


    // public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)

    {

        //  _log.LogInformation("ExternalMedia: ExternalMediaInsertActionFilter");
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
        _log.LogInformation("ExternalMedia: guid {Guid}", GuidCodec.DecodeString(guid));
        var stremioId = StremioId.Decode(GuidCodec.DecodeString(guid));
        _log.LogInformation("ExternalMedia: GOstremioIdING {Guid}", stremioId);
        var parsed = StremioId.Parse(stremioId);
        if (parsed is null)
        {
            await next();
            return;
        }

        _log.LogInformation("ExternalMedia: GOING {Guid}", guid);

        var (mediaType, Id) = parsed.Value;

        // item = FindByImdb(imdbId) as Video;
        item = FindByStremioId(stremioId) as Video;
        if (item is not null)
        {
            _log.LogInformation("ExternalMedia: Item exists");
            ReplaceGuid(context, item.Id);
            await next();
            return;
        }


        // var user = context.HttpContext.User.;
        // var user = await context.HttpContext.GetUserAsync();
        // var user = context.HttpContext.User as Jellyfin.Database.Implementations.Entities.User;
        var cfg = ExternalMediaPlugin.Instance!.Configuration;
        // var user = User.GetUserId();
        // var library = cfg.GetMovieFolder();
        // var folder = Helpers.TryGetMovieLibrary(cfg, _library);
        // // _log.LogInformation("ExternalMedia: top parent folder", folder.GetTopParent()?.Id);

        // _log.LogWarning("ExternalMedia: folder id, {Id}d", cfg.MovieFolderId);

        // if (folder is null)
        // {
        //     _log.LogWarning("ExternalMedia: No MovieFolderconfigured");
        //     await next();
        //     return;
        // }

        //  var folder = Helpers.TryGetMovieFolder(cfg, _library);
        //  _log.LogWarning("ExternalMedia: folder id, {Id}d", cfg.MovieFolderId);

        //  if (folder is null)
        //  {
        //      _log.LogWarning("ExternalMedia: No MovieFolderconfigured");
        //      await next();
        //      return;
        // }

        var folder = _manager.TryGetMovieFolder();

        if (folder is null)
        {
            _log.LogWarning("ExternalMedia: No Movie folder configured");
            await next();
            return;
        }


        // var folder = library?.GetRootFolder();

        //  await _library.AddVirtualFolder(
        //         "My External Media",
        //         CollectionTypeOptions.movies,
        //         options,
        //         refreshLibrary: false
        //     );

        _log.LogInformation("ExternalMedia: end of the road");

        if (item is null)
        {
            var meta = await _stremioProvider.GetMetaAsync(Id, mediaType).ConfigureAwait(false);
            if (meta is not null)
            {
                _log.LogInformation("ExternalMedia: ID {Guid}", meta.Id);
                _log.LogInformation("ExternalMedia: IMDBID {Guid}", meta.ImdbId);

                var _item = _stremioProvider.IntoBaseItem(meta);
                // _item.IsPlaceHolder = true;
                if (_item is not null)
                {
                    var imdbId = _item.GetProviderId(MetadataProvider.Imdb);
                    if (!string.IsNullOrEmpty(imdbId))
                    {

                        _log.LogInformation("ExternalMedia: creating item");

                        //   folder.AddChild(_item);
                        // ReplaceGuid(context, _item.Id);
                        //var streams = await _provider.GetStreamsAsync(_item).ConfigureAwait(false);
                        //var stream = streams[0];
                        // await ReplaceAlternatesTestAsync(_item, streams, CancellationToken.None);

                        var streams = await _stremioProvider.GetStreamsAsync(_item).ConfigureAwait(false);
                        await SaveAsStrm(meta, streams, CancellationToken.None);

                        var parent = _library.FindByPath("/media/external/movies", true) as Folder;
                        if (parent != null)
                        {
                            var opts = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                            {
                                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                                ReplaceAllImages = false,
                                ReplaceAllMetadata = false,
                                EnableRemoteContentProbe = false,
                            };

                            _provider.QueueRefresh(parent.Id, opts, RefreshPriority.High);
                        }

                        //var jfFolder = await _manager.EnsureIndexedWithFullMetadataAsync(
                        //    $"/media/external/movies/{meta.Name} ({meta.Year})", TimeSpan.FromSeconds(15), CancellationToken.None);
                        //_libraryMonitor.ReportFileSystemChanged($"/media/external/movies/{meta.Name} ({meta.Year})");

                        var citem = await _manager.WaitForMediaAsync(_item.ProviderIds, TimeSpan.FromSeconds(30), CancellationToken.None);
                        if (citem is not null)
                        {
                            _log.LogInformation("ExternalMedia: done scan {citem.Id}");

                            ReplaceGuid(context, citem.Id);
                        }
                        else
                        {
                            _log.LogError("ExternalMedia: media poll timeout");
                        }

                        //   await _library.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);

                    }
                    else
                    {
                        _log.LogWarning("ExternalMedia: No IMDB ID found, skipping");
                    }
                }

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
        // await _library.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);
        _log.LogInformation("ExternalMedia: end of the road----------");

        await next();
        return;
    }

    private async Task SaveAsStrm(StremioMeta meta, IEnumerable<StremioStream> streams, CancellationToken ct)
    {
        var i = 1;
        foreach (var s in streams)
        {

            // }
            await _manager.SaveStrmAsync(

                  $"/media/external/movies/{meta.Name} ({meta.Year})/{meta.Name} ({meta.Year}) - {i} {s.Name}",
                s.Url
            );
            i++;
        }
        //await _library.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);
    }
    private bool TryGetRouteGuid(ResourceExecutingContext ctx, out Guid value)
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

    private void ReplaceGuid(ResourceExecutingContext ctx, Guid value)
    {
        // value = Guid.Empty;
        var rd = ctx.RouteData.Values;
        foreach (var _key in new[] { "id", "Id", "ID", "itemId", "ItemId", "ItemID" })
        {
            if (rd.TryGetValue(_key, out var raw) && raw is not null)
            {
                _log.LogInformation("ExternalMedia: Replacing route {Key} {Old} → new: {New}", _key, raw, value);
                ctx.RouteData.Values[_key] = value.ToString();
                // ctx.RouteData.Values[_key] = "79d20a8f96c407d2ea312b8197618232";
            }
        }

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

    public BaseItem? FindByStremioId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        // take the last path segment (e.g., "tmdb:120" or "tt1119646")
        var lastSlash = id.LastIndexOf('/');
        var ext = lastSlash >= 0 ? id[(lastSlash + 1)..] : id;

        string providerKey;
        string providerValue;

        if (ext.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase))
        {
            providerKey = MetadataProvider.Tmdb.ToString();   // "Tmdb"
            providerValue = ext.Substring("tmdb:".Length);    // "120"
        }
        else if (ext.StartsWith("imdb:", StringComparison.OrdinalIgnoreCase))
        {
            providerKey = MetadataProvider.Imdb.ToString();   // "Imdb"
            providerValue = ext.Substring("imdb:".Length);    // "tt1119646"
        }
        else if (ext.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            providerKey = MetadataProvider.Imdb.ToString();   // "Imdb"
            providerValue = ext;                              // "tt1119646"
        }
        else
        {
            // Unknown id scheme
            return null;
        }

        var q = new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
            Recursive = true,
            Limit = 1,
            HasAnyProviderId = new Dictionary<string, string> { [providerKey] = providerValue }
        };

        return _library.GetItemList(q).FirstOrDefault();
    }

    private async Task ReplaceAlternatesTestAsync(BaseItem primary, IEnumerable<StremioStream> streams, CancellationToken ct)
    {
        var parent = primary.GetParent() as Folder ?? (_library.GetItemById(primary.ParentId) as Folder);
        if (parent is null)
        {
            _log.LogWarning("ExternalMedia: primary {Id} has no parent; cannot replace alternates", primary.Id);
            return;
        }
        var video = primary as Video;

        var existing = video.LinkedAlternateVersions ?? Array.Empty<LinkedChild>();
        foreach (var link in existing)
        {
            if (link.ItemId is Guid id)
            {
                try { _repo.DeleteItem(id); }
                catch (Exception ex) { _log.LogWarning(ex, "ExternalMedia: failed to delete alternate {Alt}", id); }
            }
        }
        video.LinkedAlternateVersions = Array.Empty<LinkedChild>();

        var newLinks = new List<LinkedChild>();
        foreach (var s in streams)
        {
            if (s is null || string.IsNullOrWhiteSpace(s.Url)) continue;

            var alt = BuildAlternate(video, s);
            if (alt is null) continue;

            alt.PresentationUniqueKey = alt.CreatePresentationUniqueKey();
            //parent.AddChild(alt);
            await alt.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);

            newLinks.Add(new LinkedChild { ItemId = alt.Id, Path = alt.Path });
            break;
        }

        video.LinkedAlternateVersions = newLinks.ToArray();
        await video.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
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
                //  IsRemote = true,
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
                //   IsRemote = true,
                IsVirtualItem = true,
                PrimaryVersionId = primary.Id.ToString("N"),
                // IndexNumber = epPrimary.IndexNumber,
                // ParentIndexNumber = epPrimary.ParentIndexNumber,
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