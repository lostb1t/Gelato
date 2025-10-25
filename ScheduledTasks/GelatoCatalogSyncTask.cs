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

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _log.LogInformation("Catalog sync started");

            var manifest = await _stremio.GetManifestAsync().ConfigureAwait(false);
            var catalogs = manifest?.Catalogs.Where(c => !c.IsSearchCapable()).ToList() ?? new();
            if (catalogs.Count == 0)
            {
                progress.Report(100);
                _log.LogInformation("No catalogs found");
                return;
            }

            var maxPerCatalog = GelatoPlugin.Instance!.Configuration.CatalogMaxItems;
            var seriesFolder = _manager.TryGetSeriesFolder();
            var movieFolder = _manager.TryGetMovieFolder();

            // Progress counters
            var total = Math.Max(1, catalogs.Count * maxPerCatalog);
            long done = 0;

            var opts = new ParallelOptions
            {
                MaxDegreeOfParallelism = 8,
                CancellationToken = cancellationToken
            };

            await Parallel.ForEachAsync(catalogs, opts, async (cat, ct) =>
            {
                var root = cat.Type == StremioMediaType.Series ? seriesFolder : movieFolder;
                if (root is null)
                {
                    _log.LogWarning("Catalog task: No {Type} root folder found; skipping {Type}/{Id}", cat.Type, cat.Id);
                    return;
                }

                try
                {
                    _log.LogInformation("Loading catalog: {Type} / {Id}", cat.Type, cat.Id);

                    var skip = 0;
                    var processed = 0;

                    while (processed < maxPerCatalog)
                    {
                        ct.ThrowIfCancellationRequested();

                        var page = await _stremio
                            .GetCatalogMetasAsync(cat.Id, cat.Type, search: null, skip: skip)
                            .ConfigureAwait(false);

                        if (page is null || page.Count == 0)
                            break;

                        var filterUnreleased = GelatoPlugin.Instance?.Configuration.FilterUnreleased ?? true;
                        var bufferDays = GelatoPlugin.Instance?.Configuration.FilterUnreleasedBufferDays ?? 30;

                        foreach (var _meta in page)
                        {
                            ct.ThrowIfCancellationRequested();

                            // Filter unreleased items from catalog
                            if (filterUnreleased && !_meta.IsReleased(cat.Type == StremioMediaType.Movie ? bufferDays : 0))
                            {
                                _log.LogDebug("Skipping unreleased item: {Name} ({Id})", _meta.Name, _meta.Id);
                                continue;
                            }

                            var meta = _meta;
                            if (cat.Type == StremioMediaType.Series && _meta.Videos is null)
                            {
                                meta = await _stremio.GetMetaAsync(_meta.ImdbId ?? _meta.Id, _meta.Type).ConfigureAwait(false);
                                if (meta is null)
                                {
                                    _log.LogWarning("Stremio meta not found for {Id} {Type}", _meta.Id, _meta.Type);
                                    continue;
                                }

                                // Re-check release status for the detailed meta (no buffer for TV series)
                                if (filterUnreleased && !meta.IsReleased(0))
                                {
                                    _log.LogDebug("Skipping unreleased series: {Name} ({Id})", meta.Name, meta.Id);
                                    continue;
                                }
                            }

                            try
                            {

                                var _ = await _manager
                                    .InsertMeta(root, meta, true, false, ct)
                                    .ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _log.LogError("Insert meta failed for {Id}. Exception: {Message}\n{StackTrace}",
                                    meta?.Id, ex.Message, ex.StackTrace);
                            }

                            processed++;
                            var current = Interlocked.Increment(ref done);
                            progress.Report(Math.Min(100, (current / (double)total) * 100.0));

                            if (processed >= maxPerCatalog)
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
            });

            _log.LogInformation("Catalog sync completed");
        }
    }
}