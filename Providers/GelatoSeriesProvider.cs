using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using Jellyfin.Data.Events;

namespace Gelato.Providers
{
    public sealed class GelatoSeriesProvider
        : IRemoteMetadataProvider<Series, SeriesInfo>,
          IHasOrder
    {
        private readonly ILogger<GelatoSeriesProvider> _log;
        private readonly ILibraryManager _libraryManager;
        private readonly GelatoManager _manager;
        private readonly GelatoStremioProvider _stremio;
        private readonly IProviderManager _provider;
        private readonly ConcurrentDictionary<Guid, DateTime> _syncCache = new();
        private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(2);
        
        public GelatoSeriesProvider(
            ILogger<GelatoSeriesProvider> logger,
            ILibraryManager libraryManager,
            GelatoStremioProvider stremio,
            IProviderManager provider,
            GelatoManager manager
        )
        {
            _log = logger;
            _libraryManager = libraryManager;
            _manager = manager;
            _stremio = stremio;
            _provider = provider;
            
            _provider.RefreshStarted += OnProviderManagerRefreshStarted;
        }

        public string Name => "Gelato Missing Season/Episode fetcher";

        public int Order => 0;
        
        private string ProviderName => Name;

        private async void OnProviderManagerRefreshStarted(object? sender, GenericEventArgs<BaseItem> genericEventArgs)
        {
            if (!IsEnabledForLibrary(genericEventArgs.Argument))
            {
                _log.LogInformation("{ProviderName} not enabled for {InputName}", ProviderName, genericEventArgs.Argument.Name);
                return;
            }
  
            var series = genericEventArgs.Argument as Series;
            if (series is null)
            {
                _log.LogWarning("{Name} is not a Series", genericEventArgs.Argument.Name);
                return;
            }

            // Check cache
            var now = DateTime.UtcNow;
            if (_syncCache.TryGetValue(series.Id, out var lastSync))
            {
                if (now - lastSync < CacheExpiry)
                {
                    _log.LogDebug("Skipping {Name} - synced {Seconds} seconds ago", series.Name, (now - lastSync).TotalSeconds);
                    return;
                }
            }

            var seriesFolder = _manager.TryGetSeriesFolder();
            if (seriesFolder is null)
            {
                _log.LogWarning("No series folder found");
                return;
            }

            var meta = await _stremio.GetMetaAsync(series).ConfigureAwait(false);
            if (meta is null)
            {
                _log.LogWarning("Skipping {Name} - no metadata found", series.Name);
                return;
            }

            // Update cache before syncing
            _syncCache[series.Id] = now;

            await _manager.SyncSeriesTreesAsync(seriesFolder, meta, CancellationToken.None);
            
            _log.LogInformation("Synced series tree for {Name}", series.Name);
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
                _ => item as Series
            };

            if (series == null)
            {
                _log.LogDebug("Given input is not in {@ValidTypes}: {Type}", new[] { nameof(Series), nameof(Season), nameof(Episode) }, item.GetType());
                return false;
            }

            var libraryOptions = _libraryManager.GetLibraryOptions(series);
            var typeOptions = libraryOptions.GetTypeOptions(series.GetType().Name);
            
            // Check if this metadata fetcher is enabled in the library options
            return typeOptions?.MetadataFetchers?.Contains(Name, StringComparer.OrdinalIgnoreCase) ?? false;
        }
    }
}