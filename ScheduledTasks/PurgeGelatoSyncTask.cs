// File: Tasks/GelatoCatalogSyncTask.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Gelato;
using Gelato.Common;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities;

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
        public string Category => "Maintenance";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
            // return
            // [
            //     new TaskTriggerInfo
            // {
            //     Type = TaskTriggerInfo.TriggerInterval,
            //     IntervalTicks = TimeSpan.FromHours(24).Ticks
            // }
            // ];
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
{
    _log.LogInformation("purging");

    var query = new InternalItemsQuery
    {
      
        IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
        Recursive = true
    };

    var items = _library.GetItemList(query)
        .OfType<Video>()
      //    .Where(v => !v.IsFileProtocol)     
        .Where(v => v.ProviderIds.TryGetValue("stremio", out var id) && !string.IsNullOrWhiteSpace(id) && !v.IsFileProtocol)
        .ToArray();

    int total = items.Length;
  

    int done = 0;

    foreach (var item in items)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            _library.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, true);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to delete item {ItemId}", item.Id);
        }

        done++;
        var pct = Math.Min(100.0, ((double)done / total) * 100.0);
        progress?.Report(pct);
    }
DeleteEmptyStremioContainers();
    progress?.Report(100.0);
    await _library.ValidateMediaLibrary(progress: new Progress<double>(), cancellationToken);
    _log.LogInformation("purge completed");
}
    
    
    private void DeleteEmptyStremioContainers()
{
                _log.LogInformation("YOGGOFOFF");
    var seasons = _library.GetItemList(new InternalItemsQuery
    {
        IncludeItemTypes = new[] { BaseItemKind.Season },
        Recursive = true
    }).OfType<Season>()
    //   .Where(s => s.ProviderIds.TryGetValue("stremio", out var pid) && !string.IsNullOrWhiteSpace(pid))
   //   .Where(s => s.ProviderIds.TryGetValue("stremio", out var pid) && !string.IsNullOrWhiteSpace(pid))
      .ToArray();

    foreach (var season in seasons)
    {
        var hasEpisodes = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            ParentId = season.Id,
        }).Any();

        if (!hasEpisodes)
        {
            try
            {
                _library.DeleteItem(season, new DeleteOptions { DeleteFileLocation = false }, true);
                _log.LogInformation("Deleted empty Stremio season {Name}", season.Name);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to delete season {Name}", season.Name);
            }
        }
    }

    var seriesList = _library.GetItemList(new InternalItemsQuery
    {
        IncludeItemTypes = new[] { BaseItemKind.Series },
        Recursive = true
    }).OfType<Series>()
    //  .Where(s => s.ProviderIds.TryGetValue("stremio", out var pid) && !string.IsNullOrWhiteSpace(pid))
      .ToArray();

    foreach (var series in seriesList)
    {
        var hasEpisodes = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            AncestorIds = new[] { series.Id },
        }).Any();

        if (!hasEpisodes)
        {
            try
            {
                _library.DeleteItem(series, new DeleteOptions { DeleteFileLocation = false }, true);
                _log.LogInformation("Deleted empty Stremio series {Name}", series.Name);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Failed to delete series {Name}", series.Name);
            }
        }
    }
}
}
}