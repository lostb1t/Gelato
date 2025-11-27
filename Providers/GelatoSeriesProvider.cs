using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelato.Common;
using Gelato.Configuration;
using Jellyfin.Data.Enums;
using Jellyfin.Data.Events;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Gelato.Providers
{
    public sealed class GelatoSeriesProvider
        : IRemoteMetadataProvider<Series, SeriesInfo>,
            IHasOrder
    {
        private readonly ILogger<GelatoSeriesProvider> _log;
        private readonly ILibraryManager _libraryManager;
        private readonly GelatoManager _manager;
        private readonly GelatoStremioProviderFactory _stremioFactory;
        private readonly IProviderManager _provider;
        private readonly ConcurrentDictionary<Guid, DateTime> _syncCache = new();
        private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(2);
        private readonly KeyLock _lock = new();

        public GelatoSeriesProvider(
            ILogger<GelatoSeriesProvider> logger,
            ILibraryManager libraryManager,
            GelatoStremioProviderFactory stremioFactory,
            IProviderManager provider,
            GelatoManager manager
        )
        {
            _log = logger;
            _libraryManager = libraryManager;
            _stremioFactory = stremioFactory;
            _manager = manager;

            _provider = provider;

            _provider.RefreshStarted += OnProviderManagerRefreshStarted;
        }

        public string Name => "Gelato Missing Season/Episode fetcher";

        public int Order => 0;

        private string ProviderName => Name;

        private async void OnProviderManagerRefreshStarted(
            object? sender,
            GenericEventArgs<BaseItem> genericEventArgs
        )
        {
            var cfg = GelatoPlugin.Instance!.GetConfig(Guid.Empty);
            var stremio = cfg.stremio;
            if (!await stremio.IsReady())
            {
                _log.LogWarning("gelato is not ready");
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

            var series = genericEventArgs.Argument as Series;
            if (series is null)
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

            //  if (_syncCache.TryGetValue(series.Id, out var lastSync))
            //  {
            if (now - lastSync < CacheExpiry)
            {
                _log.LogDebug(
                    "Skipping {Name} - synced {Seconds} seconds ago",
                    series.Name,
                    (now - lastSync).TotalSeconds
                );
                return;
            }
            //}

            // Update cache before syncing
            _syncCache[series.Id] = now;

            //var userIds = GelatoPlugin
            //    .Instance!.Configuration.UserConfigs.Select(x => x.UserId)
            //    .ToList();

            // main settings
            // userIds.Add(Guid.Empty);

            //foreach (var userId in userIds)
            //{

            var seriesFolder = cfg.SeriesFolder;
            if (seriesFolder is null)
            {
                _log.LogWarning("No series folder found");
                return;
            }

            //   await _lock
            //      .RunSingleFlightAsync(
            //         series.Id,
            //         async ct =>
            //         {
            try
            {
                var meta = await stremio.GetMetaAsync(series).ConfigureAwait(false);
                if (meta is null)
                {
                    _log.LogWarning("Skipping {Name} - no metadata found", series.Name);
                    return;
                }

                await _manager.SyncSeriesTreesAsync(seriesFolder, meta, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "failed sync series for {Name}", series.Name);
            }
            //   }
            //  });
            _log.LogInformation("synced series tree for {Name}", series.Name);
        }

        public async Task<MetadataResult<Series>> GetMetadata(
            SeriesInfo info,
            CancellationToken cancellationToken
        )
        {
            var result = new MetadataResult<Series> { HasMetadata = false, QueriedById = true };
            return result;
        }

        public bool SupportsSearch => false;

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
            SeriesInfo searchInfo,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult<IEnumerable<RemoteSearchResult>>(
                Array.Empty<RemoteSearchResult>()
            );
        }

        public Task<HttpResponseMessage> GetImageResponse(
            string url,
            CancellationToken cancellationToken
        )
        {
            throw new NotImplementedException();
        }

        private bool IsEnabledForLibrary(BaseItem item)
        {
            Series? series = item switch
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
}
