// File: Tasks/GelatoCatalogSyncTask.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;                // IScheduledTask, ITaskTrigger, IntervalTrigger
using Microsoft.Extensions.Logging;
using Gelato;                                  // your namespaces
using Gelato.Common;                           // if you keep helpers here

namespace Gelato.Tasks
{
    public sealed class GelatoCatalogItemsSyncTask : IScheduledTask
    {
        private readonly ILogger<GelatoCatalogItemsSyncTask> _log;
        private readonly GelatoStremioProvider _stremio;
        private readonly GelatoManager _manager; // <- put your insert/upsert logic here

        public GelatoCatalogItemsSyncTask(
            ILogger<GelatoCatalogItemsSyncTask> log,
            GelatoStremioProvider stremio,
            GelatoManager manager)
        {
            _log = log;
            _stremio = stremio;
            _manager = manager;
        }

        public string Name => "Gelato: Import catalog items";
        public string Key => "GelatoCatalogItemsSync";
        public string Description => "Loads all Stremio catalogs items and inserts/updates items in the Jellyfin database.";
        public string Category => "Gelato";

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
            _log.LogInformation("[Gelato] Catalog sync started");

            var manifest = await _stremio.GetManifestAsync().ConfigureAwait(false);
            var catalogs = manifest?.Catalogs.Where(c => !c.IsSearchCapable());
            if (catalogs.Count() == 0)
            {
                progress.Report(100);
                _log.LogInformation("[Gelato] No catalogs found");
                return;
            }

            double done = 0;
            double total = catalogs.Count();
            var max = GelatoPlugin.Instance!.Configuration.CatalogMaxItems;

            foreach (var cat in catalogs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var root = cat.Type == StremioMediaType.Series ? _manager.TryGetSeriesFolder() : _manager.TryGetMovieFolder();

                try
                {
                    _log.LogInformation("[Gelato] Loading catalog: {Type} / {Id}",
                        cat.Type, cat.Id);

                    var skip = 0;
                    var processed = 0;

                    while (processed < max)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var page = await _stremio
                            .GetCatalogMetasAsync(cat.Id, cat.Type, search: null, skip: skip)
                            .ConfigureAwait(false);

                        if (page is null || page.Count == 0)
                            break;

                        foreach (var meta in page)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            await _manager.InsertMeta(root, meta, cancellationToken).ConfigureAwait(false);
                            processed++;

                            if (processed >= max)
                                break;
                        }

                        skip += page.Count;
                    }
                    _log.LogInformation("[Gelato] Catalog {Id} synced ({Count} items)", cat.Id, processed);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[Gelato] Catalog sync failed for {Id}", cat.Id);
                }
                finally
                {
                    done++;
                    progress.Report(Math.Min(100, (done / total) * 100.0));
                }
            }

            _log.LogInformation("[Gelato] Catalog sync completed");
        }
    }
}