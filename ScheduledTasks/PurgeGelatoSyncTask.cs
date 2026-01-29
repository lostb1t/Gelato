using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelato;
using Gelato.Common;
using Gelato.Decorators;
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
    private readonly GelatoItemRepository _repo;

        public PurgeGelatoSyncTask(
            ILibraryManager libraryManager,
            ILogger<PurgeGelatoSyncTask> log,
                    GelatoItemRepository repo,  
            GelatoManager manager
        )
        {
            _log = log;
            _library = libraryManager;
            _manager = manager;
                    _repo = repo;
        }

        public string Name => "WARNING: purge all gelato items";
        public string Key => "PurgeGelatoSyncTask";
        public string Description => "Removes all stremio items (local items are kept)";
        public string Category => "Gelato Maintenance";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
        {
            var stats = new ConcurrentDictionary<BaseItemKind, int>();

            var parentQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[]
                {
                    BaseItemKind.Movie,
                    BaseItemKind.Series,
                    BaseItemKind.BoxSet,
                },
                Recursive = true,
                HasAnyProviderId = new()
                {
                    { "Stremio", string.Empty },
                    { "stremio", string.Empty },
                },
                GroupByPresentationUniqueKey = false,
                GroupBySeriesPresentationUniqueKey = false,
                CollapseBoxSetItems = false,
                IsDeadPerson = true,
            };

            var parentItems = _library
                .GetItemList(parentQuery)
                .OfType<BaseItem>()
                .Where(i => _manager.IsGelato(i))
                .ToList();

if (parentItems.Any()) {
_repo.DeleteItem(parentItems.Select(m => m.Id).ToList());
}
            var childQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Season, BaseItemKind.Episode },
                Recursive = true,
                HasAnyProviderId = new()
                {
                    { "Stremio", string.Empty },
                    { "stremio", string.Empty },
                },
                GroupByPresentationUniqueKey = false,
                GroupBySeriesPresentationUniqueKey = false,
                CollapseBoxSetItems = false,
                IsDeadPerson = true,
            };

            var childItems = _library
                .GetItemList(childQuery)
                .OfType<BaseItem>()
                .Where(i => _manager.IsGelato(i))
                .ToList();
if (childItems.Any()) {
_repo.DeleteItem(childItems.Select(m => m.Id).ToList());
}
            _manager.ClearCache();
            progress?.Report(100.0);

            var ordered = stats.OrderBy(k => k.Key.ToString());
            var parts = ordered.Select(kv => $"{kv.Key}={kv.Value}");
            var line = string.Join(", ", parts);

            _log.LogInformation("Deleted: {Stats} (Total={Total})", line, stats.Values.Sum());
        }

        
    }
}
