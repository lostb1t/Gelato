using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelato;
using Gelato.Common;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

namespace Gelato.Tasks
{
    public sealed class PurgeGelatoSyncTask : IScheduledTask
    {
        private readonly ILogger<PurgeGelatoSyncTask> _log;
        private readonly GelatoStremioProvider _stremio;
        private readonly GelatoManager _manager;
        private readonly ILibraryManager _library;

        public PurgeGelatoSyncTask(
            ILibraryManager libraryManager,
            ILogger<PurgeGelatoSyncTask> log,
            GelatoStremioProvider stremio,
            GelatoManager manager
        )
        {
            _log = log;
            _library = libraryManager;
            _stremio = stremio;
            _manager = manager;
        }

        public string Name => "WARNING purge all gelato items";
        public string Key => "PurgeGelatoSyncTask";
        public string Description => "Removes all stremio items (local items are kept)";
        public string Category => "Gelato Maintenance";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public async Task ExecuteAsync(
            IProgress<double> progress,
            CancellationToken cancellationToken
        )
        {
            _log.LogInformation("purging");

            var movie = _manager.TryGetMovieFolder();
            var series = _manager.TryGetSeriesFolder();

            var allChildren = new List<BaseItem>();

            if (movie != null)
            {
                allChildren.AddRange(movie.GetRecursiveChildren());
            }

            if (series != null)
            {
                allChildren.AddRange(series.GetRecursiveChildren());
            }
            
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                Recursive = true,
                HasAnyProviderId = new()
                {
                    { "GelatoCatalogId", string.Empty },
                },
                // skip filters marker
                IsDeadPerson = true,
            };

var collections = _library
                .GetItemList(query)
                .OfType<BoxSet>()
                .ToArray();
          allChildren.AddRange(collections);

            int total = allChildren.Count;
            int deleted = 0;

            foreach (var child in allChildren)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_manager.IsGelato(child))
                {
                    continue;
                }
                try
                {
                    _library.DeleteItem(
                        child,
                        new DeleteOptions { DeleteFileLocation = false },
                        true
                    );
                    deleted++;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to delete item {ItemId}", child.Id);
                }

                progress?.Report(Math.Min(100.0, (double)deleted / total * 100.0));
            }
            
            // collections

            _manager.ClearCache();
            progress?.Report(100.0);

            _log.LogInformation("purge completed: deleted {Count} items", deleted);
        }
    }
}
