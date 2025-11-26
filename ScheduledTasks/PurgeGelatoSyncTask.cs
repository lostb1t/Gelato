using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelato;
using Gelato.Common;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.Tasks
{
    public sealed class PurgeGelatoSyncTask : IScheduledTask
    {
        private readonly ILogger<PurgeGelatoSyncTask> _log;
        private readonly GelatoManager _manager;
        private readonly ILibraryManager _library;

        public PurgeGelatoSyncTask(
            ILibraryManager libraryManager,
            ILogger<PurgeGelatoSyncTask> log,
            GelatoManager manager
        )
        {
            _log = log;
            _library = libraryManager;
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

            //var movie = _manager.TryGetMovieFolder();
            //var series = _manager.TryGetSeriesFolder();

            var q = new InternalItemsQuery
            {
                IncludeItemTypes = new[]
                {
                    BaseItemKind.Movie,
                    BaseItemKind.Episode,
                    BaseItemKind.BoxSet,
                    BaseItemKind.Series,
                    BaseItemKind.Season,
                },
                Recursive = true,
                HasAnyProviderId = new()
                {
                    { "Stremio", string.Empty },
                    { "stremio", string.Empty },
                    // deprecated
                    { "GelatoCatalogId", string.Empty },
                },
                GroupByPresentationUniqueKey = false,
                GroupBySeriesPresentationUniqueKey = false,
                CollapseBoxSetItems = false,
                // skip filter marker
                IsDeadPerson = true,
            };

            var items = _library
                .GetItemList(q)
                .OfType<BaseItem>()
                .OrderBy(item =>
                {
                    if (item is Series)
                        return 2; // Absolute last
                    if (item is Season)
                        return 1; // Second last
                    return 0;
                });

            int total = items.Count();
            int deleted = 0;

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_manager.IsGelato(item))
                {
                    continue;
                }

                if (
                    (item is Season || item is Series)
                    && item is Folder folder
                    && folder.GetRecursiveChildren().Any()
                )
                {
                    continue;
                }

                try
                {
                    _library.DeleteItem(
                        item,
                        new DeleteOptions { DeleteFileLocation = false },
                        true
                    );
                    deleted++;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to delete item {ItemId}", item.Id);
                }

                progress?.Report(Math.Min(100.0, (double)deleted / total * 100.0));
            }

            _manager.ClearCache();
            progress?.Report(100.0);

            _log.LogInformation("purge completed: deleted {Count} items", deleted);
        }
    }
}
