using System;
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

        public GelatoSeriesProvider(
            ILogger<GelatoSeriesProvider> logger,
            ILibraryManager libraryManager,
            GelatoStremioProvider stremio,
            GelatoManager manager
        )
        {
            _log = logger;
            _libraryManager = libraryManager;
            _manager = manager;
            _stremio = stremio;
        }

        public string Name => "Gelato Series Sync";

        public int Order => 0;

        public async Task<MetadataResult<Series>> GetMetadata(
            SeriesInfo info,
            CancellationToken cancellationToken
        )
        {
            var result = new MetadataResult<Series> { HasMetadata = false };
            var seriesFolder = _manager.TryGetSeriesFolder();
            if (seriesFolder is null)
            {
                _log.LogWarning($"no series folder found");
                return result;
            }

            var series = _manager.GetByProviderIds(info.ProviderIds, BaseItemKind.Series);
            if (series is null)
            {
                _log.LogWarning($"{info.Name} not found");
                return result;
            }

            var meta = await _stremio.GetMetaAsync(series).ConfigureAwait(false);
            if (meta is null)
            {
                _log.LogWarning("skipping {Name} - no metadata found", info.Name);
                return result;
            }
            Console.Write("URIDEI");
            await _manager.SyncSeriesTreesAsync(seriesFolder, meta, cancellationToken);

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
    }
}
