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
            GelatoManager manager)
        {
            _log = log;
            _library = libraryManager;
            _stremio = stremio;
            _manager = manager;
        }

        public string Name => "Gelato: WARNING purge all items";
        public string Key => "PurgeGelatoSyncTask";
        public string Description => "Removes all stremio items (local items are kept)";
        public string Category => "Gelato Maintenance";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => 
            Array.Empty<TaskTriggerInfo>();

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _log.LogInformation("purging");

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                Recursive = true,
                GroupByPresentationUniqueKey = false,
                GroupBySeriesPresentationUniqueKey = false,
                CollapseBoxSetItems = false,
                HasAnyProviderId = new()
                {
                    { "Stremio", string.Empty },
                    { "stremio", string.Empty },
                },
                IsDeadPerson = true,
            };

            var items = _library
                .GetItemList(query)
                .OfType<Video>()
                .Where(v => !v.IsFileProtocol)
                .ToArray();

            int deletedItems = DeleteItems(items, progress, cancellationToken);
            var (deletedSeasons, deletedSeries) = DeleteEmptyStremioContainers();
            
            _manager.ClearCache();
            progress?.Report(100.0);
            
            _log.LogInformation(
                "purge completed: deleted {Items} items, {Seasons} seasons, {Series} series",
                deletedItems,
                deletedSeasons,
                deletedSeries);
        }

        private int DeleteItems(Video[] items, IProgress<double> progress, CancellationToken cancellationToken)
        {
            int total = items.Length;
            int deleted = 0;

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    _library.DeleteItem(
                        item,
                        new DeleteOptions { DeleteFileLocation = false },
                        true);
                    deleted++;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to delete item {ItemId}", item.Id);
                }

                progress?.Report(Math.Min(100.0, (double)(deleted) / total * 100.0));
            }

            return deleted;
        }

        private (int deletedSeasons, int deletedSeries) DeleteEmptyStremioContainers()
        {
            int deletedSeasons = DeleteEmptySeasons();
            int deletedSeries = DeleteEmptySeries();
            return (deletedSeasons, deletedSeries);
        }

        private int DeleteEmptySeasons()
        {
            var seasons = _library
                .GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Season },
                    Recursive = true,
                })
                .OfType<Season>()
                .ToArray();

            int deleted = 0;
            foreach (var season in seasons)
            {
                if (!HasEpisodes(season.Id, isParent: true))
                {
                    if (TryDeleteItem(season, "season"))
                        deleted++;
                }
            }

            return deleted;
        }

        private int DeleteEmptySeries()
        {
            var seriesList = _library
                .GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Series },
                    Recursive = true,
                })
                .OfType<Series>()
                .ToArray();

            int deleted = 0;
            foreach (var series in seriesList)
            {
                if (!HasEpisodes(series.Id, isParent: false))
                {
                    if (TryDeleteItem(series, "series"))
                        deleted++;
                }
            }

            return deleted;
        }

        private bool HasEpisodes(Guid id, bool isParent)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                IsDeadPerson = true,
                Recursive = true,
                GroupByPresentationUniqueKey = false,
                GroupBySeriesPresentationUniqueKey = false,
                CollapseBoxSetItems = false,
            };

            if (isParent)
                query.ParentId = id;
            else
                query.AncestorIds = new[] { id };

            return _library.GetItemList(query).Any();
        }

        private bool TryDeleteItem(BaseItem item, string itemType)
        {
            try
            {
                _library.DeleteItem(
                    item,
                    new DeleteOptions { DeleteFileLocation = false },
                    true);
                _log.LogInformation("deleted empty {Type} {Name}", itemType, item.Name);
                return true;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to delete {Type} {Name}", itemType, item.Name);
                return false;
            }
        }
    }
}