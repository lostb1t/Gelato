using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using Jellyfin.Data.Events;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Gelato.Providers;

public sealed class GelatoSeriesProvider : IRemoteMetadataProvider<Series, SeriesInfo>, IHasOrder
{
    private readonly ILogger<GelatoSeriesProvider> _log;
    private readonly ILibraryManager _libraryManager;
    private readonly GelatoManager _manager;
    private readonly IProviderManager _provider;
    private readonly ConcurrentDictionary<Guid, DateTime> _syncCache = new();
    private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(2);

    public GelatoSeriesProvider(
        ILogger<GelatoSeriesProvider> logger,
        ILibraryManager libraryManager,
        IProviderManager provider,
        GelatoManager manager
    )
    {
        _log = logger;
        _libraryManager = libraryManager;
        _manager = manager;
        _provider = provider;

        _provider.RefreshStarted += OnProviderManagerRefreshStarted;
    }

    public string Name => "Gelato";

    public int Order => 0;

    private string ProviderName => Name;

    private async void OnProviderManagerRefreshStarted(
        object? sender,
        GenericEventArgs<BaseItem> genericEventArgs
    )
    {
        var cfg = GelatoPlugin.Instance!.GetConfig(Guid.Empty);
        var stremio = cfg.Stremio;
        if (stremio == null)
        {
            _log.LogWarning("Gelato not configured (stremio provider missing); skipping refresh.");
            return;
        }

        if (!await stremio.IsReady().ConfigureAwait(false))
        {
            _log.LogWarning("Gelato is not ready");
            return;
        }

        if (!IsEnabledForLibrary(genericEventArgs.Argument))
        {
            _log.LogTrace(
                "{ProviderName} not enabled for {InputName}",
                ProviderName,
                genericEventArgs.Argument.Name
            );
            return;
        }

        if (genericEventArgs.Argument is not Series series)
        {
            _log.LogTrace("{Name} is not a Series", genericEventArgs.Argument.Name);
            return;
        }

        // Check cache
        var now = DateTime.UtcNow;
        if (!_syncCache.TryGetValue(series.Id, out var lastSync))
        {
            lastSync = genericEventArgs.Argument.DateLastSaved;
        }

        if (now - lastSync < CacheExpiry)
        {
            _log.LogDebug(
                "Skipping {Name} - synced {Seconds} seconds ago",
                series.Name,
                (now - lastSync).TotalSeconds
            );
            return;
        }

        // Update cache before syncing
        _syncCache[series.Id] = now;

        var seriesFolder = cfg.SeriesFolder;
        if (seriesFolder is null)
        {
            _log.LogWarning("No series folder found");
            return;
        }

        try
        {
            var meta = await stremio.GetMetaAsync(series).ConfigureAwait(false);
            if (meta is null)
            {
                _log.LogWarning("Skipping {Name} - no metadata found", series.Name);
                return;
            }

            await _manager.SyncSeriesTreesAsync(cfg, meta, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "failed sync series for {Name}", series.Name);
        }
        _log.LogInformation("synced series tree for {Name}", series.Name);
    }

    public async Task<MetadataResult<Series>> GetMetadata(
        SeriesInfo info,
        CancellationToken cancellationToken
    )
    {
        var result = new MetadataResult<Series> { HasMetadata = false, QueriedById = true };

        var id = ResolveId(info.ProviderIds);
        if (id is null)
        {
            _log.LogDebug("GelatoSeriesProvider: no usable ID for {Name}", info.Name);
            return result;
        }

        var stremio = GelatoPlugin.Instance?.Configuration.Stremio;
        if (stremio is null)
            return result;

        StremioMeta? meta;
        try
        {
            meta =
                stremio.GetCachedMeta(id)
                ?? await stremio.GetMetaAsync(id, StremioMediaType.Series).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GelatoSeriesProvider: failed to fetch meta for {Id}", id);
            return result;
        }

        if (meta is null || !meta.IsValid())
            return result;

        stremio.CacheMeta(id, meta);

        if (_manager.IntoBaseItem(meta) is not Series series)
            return result;

        result.HasMetadata = true;
        result.Item = series;
        MapPeople(meta, result);
        return result;
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
        SeriesInfo searchInfo,
        CancellationToken cancellationToken
    )
    {
        var stremio = GelatoPlugin.Instance?.Configuration.Stremio;
        if (stremio is null || string.IsNullOrWhiteSpace(searchInfo.Name))
            return [];

        try
        {
            var results = await stremio
                .SearchAsync(searchInfo.Name, StremioMediaType.Series)
                .ConfigureAwait(false);
            return results.Select(ToSearchResult);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "GelatoSeriesProvider: search failed for {Name}", searchInfo.Name);
            return [];
        }
    }

    public Task<HttpResponseMessage> GetImageResponse(
        string url,
        CancellationToken cancellationToken
    )
    {
        throw new NotImplementedException();
    }

    private static void MapPeople(StremioMeta meta, MetadataResult<Series> result)
    {
        if (meta.App_Extras?.Cast is not { Count: > 0 } cast)
            return;

        foreach (var member in cast)
        {
            if (string.IsNullOrWhiteSpace(member.Name))
                continue;

            result.AddPerson(
                new PersonInfo
                {
                    Name = member.Name,
                    Role = member.Character,
                    Type = PersonKind.Actor,
                    ImageUrl = member.Photo,
                }
            );
        }
    }

    private static RemoteSearchResult ToSearchResult(StremioMeta meta) =>
        new()
        {
            Name = meta.GetName(),
            ProductionYear = meta.GetYear(),
            ImageUrl = meta.Poster ?? meta.Thumbnail,
            ProviderIds = meta.GetProviderIds(),
        };

    private static string? ResolveId(Dictionary<string, string> providerIds)
    {
        if (
            providerIds.TryGetValue(MetadataProvider.Imdb.ToString(), out var imdb)
            && !string.IsNullOrWhiteSpace(imdb)
        )
            return imdb;
        if (
            providerIds.TryGetValue(MetadataProvider.Tmdb.ToString(), out var tmdb)
            && !string.IsNullOrWhiteSpace(tmdb)
        )
            return $"tmdb:{tmdb}";
        return null;
    }

    private bool IsEnabledForLibrary(BaseItem item)
    {
        var series = item switch
        {
            Episode episode => episode.Series,
            Season season => season.Series,
            _ => item as Series,
        };

        if (series == null)
        {
            _log.LogTrace(
                "Given input is not in {@ValidTypes}: {Type}",
                new[] { nameof(Series), nameof(Season), nameof(Episode) },
                item.GetType()
            );
            return false;
        }

        var libraryOptions = _libraryManager.GetLibraryOptions(series);
        var typeOptions = libraryOptions.GetTypeOptions(series.GetType().Name);

        // Check if this metadata fetcher is enabled in the library options
        return typeOptions?.MetadataFetchers?.Contains(Name, StringComparer.OrdinalIgnoreCase)
            ?? false;
    }
}
