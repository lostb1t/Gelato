// File: Tasks/GelatoCatalogSyncTask.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelato;
using Gelato.Common;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.Tasks
{
    public sealed class GelatoCatalogItemsSyncTask : IScheduledTask
    {
        private readonly ILogger<GelatoCatalogItemsSyncTask> _log;
        private readonly GelatoStremioProvider _stremio;
        private readonly GelatoManager _manager;
        private readonly ILibraryManager _library;

        public GelatoCatalogItemsSyncTask(
            ILibraryManager libraryManager,
            ILogger<GelatoCatalogItemsSyncTask> log,
            GelatoStremioProvider stremio,
            GelatoManager manager)
        {
            _log = log;
            _library = libraryManager;
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
            _log.LogInformation("Catalog sync started");

            var manifest = await _stremio.GetManifestAsync().ConfigureAwait(false);
            var catalogs = manifest?.Catalogs.Where(c => !c.IsSearchCapable());
            if (catalogs.Count() == 0)
            {
                progress.Report(100);
                _log.LogInformation("No catalogs found");
                return;
            }

            double done = 0;
            var max = GelatoPlugin.Instance!.Configuration.CatalogMaxItems;
            int total = catalogs.Count() * max;
            var seriesFolder = _manager.TryGetSeriesFolder();
            var movieFolder = _manager.TryGetMovieFolder();

            foreach (var cat in catalogs)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var root = cat.Type == StremioMediaType.Series ? seriesFolder : movieFolder;

                if (root is null)
                {
                    _log.LogWarning("Catalog task: No movie or series root folder found skipping");
                    continue;
                }

                try
                {
                    _log.LogInformation("Loading catalog: {Type} / {Id}",
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
                            try
                            {
                                var (item, created) = await _manager.InsertMeta(root, meta, true, false, cancellationToken).ConfigureAwait(false);


                            }
                            catch (Exception ex)
                            {
                                _log.LogWarning(ex, "insert meta failed for {Id}", meta.Id);
                            }
                            processed++;
                            done++;
                            progress.Report(Math.Min(100, (done / total) * 100.0));

                            if (processed >= max)
                                break;
                        }

                        skip += page.Count;
                    }
                    _log.LogInformation("Catalog {Id} synced ({Count} items)", cat.Id, processed);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Catalog sync failed for {Id}", cat.Id);
                }
                finally
                {

                }
            }
            // _library.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None);
            _log.LogInformation("[Gelato] Catalog sync completed");
        }
    }
}