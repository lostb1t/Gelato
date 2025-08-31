using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Jellyfin.Plugin.ExternalMedia.Common;
using MediaBrowser.Model.IO;
using MediaBrowser.Controller.Dto;
using Jellyfin.Data.Enums;

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
    private readonly ExternalMediaSeriesManager _seriesManager;
    private readonly ExternalMediaRefresh _refresh;



    private readonly IProviderManager _provider;
    private readonly IFileSystem _fileSystem;
    private readonly ILibraryMonitor _libraryMonitor;

    public ExternalMediaInsertActionFilter(
        ILibraryManager library,
        ExternalMediaRefresh refresh,
        IFileSystem fileSystem,
        IItemRepository repo,
        IMediaSourceManager mediaSources,
        ExternalMediaManager manager,
          ExternalMediaSeriesManager seriesManager,
        IDtoService dtoService,
        ExternalMediaStremioProvider stremioProvider,
        IProviderManager provider,
        ILibraryMonitor libraryMonitor,
        ILogger<ExternalMediaInsertActionFilter> log)
    {
        _library = library;
        _refresh = refresh;
        _repo = repo;
        _mediaSources = mediaSources;
        _dtoService = dtoService;
        _provider = provider;
        _stremioProvider = stremioProvider;
        _fileSystem = fileSystem;
        _manager = manager;
        _seriesManager = seriesManager;
        _log = log;
        _libraryMonitor = libraryMonitor;
    }

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (!TryGetRouteGuid(context, out var guid))
        {
            await next();
            return;
        }

        var existing = _library.GetItemById(guid);
        if (existing is not null)
        {
            await next();
            return;
        }

        var stremioId = StremioId.Decode(GuidCodec.DecodeString(guid));
        var parsed = StremioId.Parse(stremioId);
        if (parsed is null)
        {
            await next();
            return;
        }

        var (mediaType, Id) = parsed.Value;

        var found = FindByStremioId(stremioId) as Video;
        if (found is not null)
        {
            ReplaceGuid(context, found.Id);
            await next();
            return;
        }

        bool isSeries = mediaType == StremioMediaType.Series;
        var root = isSeries ? _manager.TryGetSeriesFolder() : _manager.TryGetMovieFolder();
        if (root is null)
        {
            _log.LogWarning("ExternalMedia: No {Type} folder configured", isSeries ? "Series" : "Movie");
            await next();
            return;
        }

        var meta = await _stremioProvider.GetMetaAsync(Id, mediaType).ConfigureAwait(false);
        if (meta is null)
        {
            _log.LogWarning("ExternalMedia: Meta not found for {0} {1}", Id, mediaType);
            await next();
            return;
        }

        var baseItem = _stremioProvider.IntoBaseItem(meta);
        if (baseItem is null || baseItem.ProviderIds is null || baseItem.ProviderIds.Count == 0)
        {
            _log.LogWarning("ExternalMedia: Missing provider ids, skipping");
            await next();
            return;
        }



        Func<CancellationToken, Task<bool>> saver = isSeries
            ? (ct => _seriesManager.CreateSeriesTreesAsync(root, meta, ct))
            : (async ct =>
            {
                var streams = await _stremioProvider.GetStreamsAsync(baseItem).ConfigureAwait(false);
                await _manager.SaveStreamAsStrm(root, meta, streams, ct);
                return true;
            });

        var timeout = TimeSpan.FromSeconds(isSeries ? 45 : 30);
        var created = await MaterializeAndResolveAsync(root, baseItem, saver, timeout, CancellationToken.None).ConfigureAwait(false);

        if (created is not null)
        {
            ReplaceGuid(context, created.Id);
            StartRefreshDettached(created);
        }
        else
        {
            _log.LogError("ExternalMedia: media poll timeout");
        }

        await next();
    }

    public void StartRefreshDettached(BaseItem root)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // wait 10 seconds before starting the refresh
                await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

                using var cts = new CancellationTokenSource();
                await _refresh.RefreshAsync(root, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "ExternalMedia: background refresh failed for {Name}", root.Name);
            }
        });
    }

    private async Task<BaseItem?> MaterializeAndResolveAsync(
        BaseItem root,
        BaseItem baseItem,
        Func<CancellationToken, Task<bool>> saver,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var ok = await saver(ct).ConfigureAwait(false);
        if (!ok) return null;

        QueueParentRefresh(root);

        var resolved = await _manager.WaitForMediaAsync(baseItem.ProviderIds, timeout, ct).ConfigureAwait(false);
        return resolved;
    }

    private void QueueParentRefresh(BaseItem parent)
    {
        var folder = parent as Folder ?? (_library.GetItemById(parent.ParentId) as Folder);
        if (folder is null) return;

        var opts = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllImages = false,
            ReplaceAllMetadata = false,
            EnableRemoteContentProbe = false
        };

        _provider.QueueRefresh(folder.Id, opts, RefreshPriority.High);
    }

    private bool TryGetRouteGuid(ResourceExecutingContext ctx, out Guid value)
    {
        value = Guid.Empty;
        var rd = ctx.RouteData.Values;

        foreach (var key in new[] { "id", "Id", "ID", "itemId", "ItemId", "ItemID" })
        {
            if (rd.TryGetValue(key, out var raw) && raw is not null)
            {
                var s = raw.ToString();
                if (!string.IsNullOrWhiteSpace(s) && Guid.TryParse(s, out value))
                    return true;
            }
        }

        return false;
    }

    private void ReplaceGuid(ResourceExecutingContext ctx, Guid value)
    {
        var rd = ctx.RouteData.Values;
        foreach (var key in new[] { "id", "Id", "ID", "itemId", "ItemId", "ItemID" })
        {
            if (rd.TryGetValue(key, out var raw) && raw is not null)
            {
                _log.LogInformation("ExternalMedia: Replacing route {Key} {Old} → {New}", key, raw, value);
                ctx.RouteData.Values[key] = value.ToString();
            }
        }
    }

    public BaseItem? FindByStremioId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var lastSlash = id.LastIndexOf('/');
        var ext = lastSlash >= 0 ? id[(lastSlash + 1)..] : id;

        string providerKey;
        string providerValue;

        if (ext.StartsWith("tmdb:", StringComparison.OrdinalIgnoreCase))
        {
            providerKey = MetadataProvider.Tmdb.ToString();
            providerValue = ext.Substring("tmdb:".Length);
        }
        else if (ext.StartsWith("imdb:", StringComparison.OrdinalIgnoreCase))
        {
            providerKey = MetadataProvider.Imdb.ToString();
            providerValue = ext.Substring("imdb:".Length);
        }
        else if (ext.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            providerKey = MetadataProvider.Imdb.ToString();
            providerValue = ext;
        }
        else
        {
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
}