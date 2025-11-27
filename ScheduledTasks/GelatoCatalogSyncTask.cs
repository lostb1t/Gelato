using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelato;
using Gelato.Common;
using Gelato.Configuration;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Gelato.Tasks
{
    public sealed class GelatoCatalogItemsSyncTask : IScheduledTask
    {
        private readonly ILogger<GelatoCatalogItemsSyncTask> _log;
        private readonly GelatoManager _manager;
        private readonly ILibraryManager _library;
        private readonly ICollectionManager _collections;

        public GelatoCatalogItemsSyncTask(
            ILibraryManager libraryManager,
            ICollectionManager collections,
            ILogger<GelatoCatalogItemsSyncTask> log,
            GelatoManager manager
        )
        {
            _log = log;
            _library = libraryManager;
            _manager = manager;
            _collections = collections;
        }

        public string Name => "Import catalogs";
        public string Key => "GelatoCatalogItemsSync";
        public string Description =>
            "Loads all Stremio catalogs items and inserts/updates items in the Jellyfin database.";
        public string Category => "Gelato";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
        {
            var cfg = GelatoPlugin.Instance!.GetConfig(Guid.Empty);
            var stremio = cfg.stremio;
            var manifest = await stremio.GetManifestAsync().ConfigureAwait(false);
            var catalogs = manifest?.Catalogs.Where(c => !c.IsSearchCapable()).ToList() ?? new();
            if (catalogs.Count == 0)
            {
                progress.Report(100);
                _log.LogInformation("No catalogs found");
                return;
            }

            _log.LogInformation("processing {Count} catalogs", catalogs.Count);

            var maxPerCatalog = cfg.CatalogMaxItems;
            var seriesFolder = cfg.SeriesFolder;
            var movieFolder = cfg.MovieFolder;
            var createCollections = cfg.CreateCollections;
            var collectionMaxItems = cfg.MaxCollectionItems;

            if (seriesFolder is null)
            {
                _log.LogWarning("No series root folder found");
            }

            if (movieFolder is null)
            {
                _log.LogWarning("No movie root folder found");
            }

            // Progress counters
            var total = Math.Max(1, catalogs.Count * maxPerCatalog);
            long done = 0;
            progress.Report(done);

            var opts = new ParallelOptions { MaxDegreeOfParallelism = 10, CancellationToken = ct };

            var stopwatch = Stopwatch.StartNew();

            foreach (var cat in catalogs)
            {
                _log.LogInformation("Processing catalog: {Type} / {Id}", cat.Type, cat.Id);

                try
                {
                    var skip = 0;
                    var processed = 0;
                    var collectionCommited = false;
                    var addToCollectionIds = new List<Guid>();
                    var genreExtra = cat.Extra?.FirstOrDefault(e =>
                        string.Equals(e.Name, "genre", StringComparison.OrdinalIgnoreCase)
                    );

                    var shouldCreateCollection =
                        !(genreExtra?.IsRequired == true)
                        && createCollections
                        && !collectionCommited;
                    while (processed < maxPerCatalog)
                    {
                        ct.ThrowIfCancellationRequested();

                        var page = await stremio
                            .GetCatalogMetasAsync(cat.Id, cat.Type, search: null, skip: skip)
                            .ConfigureAwait(false);

                        if (page is null || page.Count == 0)
                        {
                            break;
                        }

                        await Parallel.ForEachAsync(
                            page,
                            opts,
                            async (meta, ct) =>
                            {
                                var p = Interlocked.Increment(ref processed);
                                ct.ThrowIfCancellationRequested();
                                if (p > maxPerCatalog)
                                {
                                    return;
                                }
                                var mediaType = meta.Type;
                                var baseItemKind = mediaType.ToBaseItem();

                                // catalog can contain multiple types.

                                var root =
                                    baseItemKind == BaseItemKind.Series ? seriesFolder
                                    : baseItemKind == BaseItemKind.Movie ? movieFolder
                                    : null;

                                if (root is not null)
                                {
                                    try
                                    {
                                        var (item, created) = await _manager
                                            .InsertMeta(
                                                root,
                                                meta,
                                                Guid.Empty,
                                                true,
                                                true,
                                                true,
                                                ct
                                            )
                                            .ConfigureAwait(false);

                                        if (item != null && shouldCreateCollection)
                                        {
                                            addToCollectionIds.Add(item.Id);
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        _log.LogError(
                                            "{CatId}: insert meta failed for {Id}. Exception: {Message}\n{StackTrace}",
                                            cat.Id,
                                            meta?.Id,
                                            ex.Message,
                                            ex.StackTrace
                                        );
                                    }
                                }

                                var current = Interlocked.Increment(ref done);
                                progress.Report(Math.Min(100, (current / (double)total) * 100.0));
                            }
                        );

                        skip += page.Count;
                    }

                    if (shouldCreateCollection && addToCollectionIds.Count != 0)
                    {
                        await SaveCollection(cat, addToCollectionIds).ConfigureAwait(false);
                        addToCollectionIds.Clear();
                    }

                    _log.LogInformation("{Id}: processed ({Count} items)", cat.Id, processed);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.LogError(
                        ex,
                        "Catalog sync failed for {Id}: {Message}",
                        cat.Id,
                        ex.Message
                    );
                }
            }
            ;
            stopwatch.Stop();
            _log.LogInformation(
                "Catalog sync completed in {Minutes}m {Seconds}s ({TotalSeconds:F2}s total)",
                (int)stopwatch.Elapsed.TotalMinutes,
                stopwatch.Elapsed.Seconds,
                stopwatch.Elapsed.TotalSeconds
            );
        }

        private async Task<BoxSet?> GetOrCreateBoxSetByIdAsync(string id, string name)
        {
            var collection = _library
                .GetItemList(
                    new InternalItemsQuery
                    {
                        IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                        CollapseBoxSetItems = false,
                        Recursive = true,
                        HasAnyProviderId = new Dictionary<string, string>
                        {
                            { "Stremio", id },
                            // deprecated
                            { "GelatoCatalogId", id },
                        },
                    }
                )
                .Select(b => b as BoxSet)
                .FirstOrDefault();

            if (collection is null)
            {
                collection = await _collections
                    .CreateCollectionAsync(
                        new CollectionCreationOptions
                        {
                            Name = name,
                            ProviderIds = new Dictionary<string, string> { { "Stremio", id } },
                        }
                    )
                    .ConfigureAwait(false);
            }

            return collection;
        }

        private async Task SaveCollection(StremioCatalog cat, List<Guid> ids)
        {
            var collection = await GetOrCreateBoxSetByIdAsync(cat.Id, cat.Name)
                .ConfigureAwait(false);

            if (collection != null)
            {
                var childrenIds = _library
                    .GetItemList(new InternalItemsQuery { Parent = collection, Recursive = false })
                    .Select(i => i.Id)
                    .ToList();

                await _collections
                    .RemoveFromCollectionAsync(collection.Id, childrenIds)
                    .ConfigureAwait(false);

                await _collections.AddToCollectionAsync(collection.Id, ids).ConfigureAwait(false);

                _log.LogInformation("{Id}: added {Count} items to collection", cat.Id, ids.Count);
            }
        }
    }
}
