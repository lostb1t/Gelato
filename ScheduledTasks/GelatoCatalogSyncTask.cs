using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gelato;
using Gelato.Common;
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
        private readonly GelatoStremioProvider _stremio;
        private readonly GelatoManager _manager;
        private readonly ILibraryManager _library;
        private readonly ICollectionManager _collections;

        public GelatoCatalogItemsSyncTask(
            ILibraryManager libraryManager,
            ICollectionManager collections,
            ILogger<GelatoCatalogItemsSyncTask> log,
            GelatoStremioProvider stremio,
            GelatoManager manager
        )
        {
            _log = log;
            _library = libraryManager;
            _stremio = stremio;
            _manager = manager;
            _collections = collections;
        }

        public string Name => "Gelato: Catalogs import";
        public string Key => "GelatoCatalogItemsSync";
        public string Description =>
            "Loads all Stremio catalogs items and inserts/updates items in the Jellyfin database.";
        public string Category => "Gelato";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

        public async Task ExecuteAsync(
            IProgress<double> progress,
            CancellationToken cancellationToken
        )
        {
            _log.LogInformation("catalog sync started");

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
            var createCollections = GelatoPlugin.Instance!.Configuration.CreateCollections;
            var collectionMaxItems = GelatoPlugin.Instance!.Configuration.MaxCollectionItems;

            // Progress counters
            var total = Math.Max(1, catalogs.Count * maxPerCatalog);
            long done = 0;

            var opts = new ParallelOptions
            {
                MaxDegreeOfParallelism = 5,
                CancellationToken = cancellationToken,
            };

            await Parallel.ForEachAsync(
                catalogs,
                opts,
                async (cat, ct) =>
                {
                    _log.LogInformation("Processing catalog: {Type} / {Id}", cat.Type, cat.Id);

                    try
                    {
                        var skip = 0;
                        var processed = 0;
                        var collectionCommited = false;
                        var addToCollectionIds = new List<Guid>();

                        while (processed < maxPerCatalog)
                        {
                            ct.ThrowIfCancellationRequested();

                            var page = await _stremio
                                .GetCatalogMetasAsync(cat.Id, cat.Type, search: null, skip: skip)
                                .ConfigureAwait(false);

                            if (page is null || page.Count == 0)
                            {
                                break;
                            }

                            foreach (var meta in page)
                            {
                                ct.ThrowIfCancellationRequested();

                                var mediaType = meta.Type;
                                var baseItemKind = mediaType.ToBaseItem();

                                var root =
                                    baseItemKind == BaseItemKind.Series ? seriesFolder
                                    : baseItemKind == BaseItemKind.Movie ? movieFolder
                                    : null;

                                if (root is null)
                                {
                                    _log.LogWarning(
                                        "Catalog task: No {Type} root folder found; skipping {Type}/{Id}",
                                        mediaType,
                                        cat.Id
                                    );
                                    continue;
                                }

                                try
                                {
                                    var (item, created) = await _manager
                                        .InsertMeta(root, meta, true, true, false, ct)
                                        .ConfigureAwait(false);

                                    if (item != null && createCollections && !collectionCommited)
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

                                processed++;
                                var current = Interlocked.Increment(ref done);
                                progress.Report(Math.Min(100, (current / (double)total) * 100.0));

                                if (
                                    createCollections
                                    && !collectionCommited
                                    && addToCollectionIds.Count >= collectionMaxItems
                                )
                                {
                                    await SaveCollection(cat, addToCollectionIds)
                                        .ConfigureAwait(false);
                                    addToCollectionIds.Clear();
                                    collectionCommited = true;
                                }

                                if (processed >= maxPerCatalog)
                                {
                                    break;
                                }
                            }

                            skip += page.Count;
                        }

                        if (
                            createCollections
                            && addToCollectionIds.Count != 0
                            && !collectionCommited
                        )
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
            );

            _log.LogInformation("Catalog sync completed");
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
                            ProviderIds = new Dictionary<string, string>
                            {
                                { "GelatoCatalogId", id },
                            },
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
