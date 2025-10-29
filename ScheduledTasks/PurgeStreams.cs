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
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.Tasks
{
    public sealed class PurgeGelatoStreamsTask : IScheduledTask
    {
        private readonly ILogger<PurgeGelatoStreamsTask> _log;
        private readonly GelatoStremioProvider _stremio;
        private readonly GelatoManager _manager;
        private readonly ILibraryManager _library;
        private readonly IItemRepository _repo;

        public PurgeGelatoStreamsTask(
            ILibraryManager libraryManager,
            ILogger<PurgeGelatoStreamsTask> log,
            GelatoStremioProvider stremio,
            IItemRepository repo,
            GelatoManager manager
        )
        {
            _log = log;
            _library = libraryManager;
            _stremio = stremio;
            _manager = manager;
            _repo = repo;
        }

        public string Name => "Gelato: purge all streams";
        public string Key => "PurgeGelatoStreamsTask";
        public string Description => "Removes all stremio streams";
        public string Category => "Gelato Maintenance";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task ExecuteAsync(
            IProgress<double> progress,
            CancellationToken cancellationToken
        )
        {
            _log.LogInformation("purging streams");

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                Recursive = false,
                HasAnyProviderId = new()
                {
                    { "Stremio", string.Empty },
                    { "stremio", string.Empty },
                },
            };

            var items = _library
                .GetItemList(query)
                .OfType<Video>()
                .Where(v => !v.IsFileProtocol && !string.IsNullOrWhiteSpace(v.PrimaryVersionId))
                .ToArray();

            int total = items.Length;

            int done = 0;

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    _repo.DeleteItem([item.Id]);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Failed to delete item {ItemId}", item.Id);
                }

                done++;
                var pct = Math.Min(100.0, ((double)done / total) * 100.0);
                progress?.Report(pct);
            }

            progress?.Report(100.0);
            _manager.ClearCache();
            //await _library.ValidateMediaLibrary(
            //    progress: new Progress<double>(),
            //     cancellationToken
            //);

            _log.LogInformation("stream purge completed");
        }
    }
}
