using Gelato.Configuration;
using Gelato.Services;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Jellyfin.Data.Enums; // Potentially needed for BaseItemKind
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using MediaBrowser.Controller.Entities.Movies;
using System.Diagnostics;
using Gelato.Common; // For BoxSet

namespace Gelato.Services;

public class CatalogImportService {
    private readonly ILogger<CatalogImportService> _log;
    private readonly GelatoManager _manager;
    private readonly GelatoStremioProviderFactory _stremioFactory;
    private readonly CatalogService _catalogService;
    private readonly ICollectionManager _collectionManager;
    private readonly ILibraryManager _libraryManager;

    public CatalogImportService(
        ILogger<CatalogImportService> logger,
        GelatoManager manager,
        GelatoStremioProviderFactory stremioFactory,
        CatalogService catalogService,
        ICollectionManager collectionManager,
        ILibraryManager libraryManager
    ) {
        _log = logger;
        _manager = manager;
        _stremioFactory = stremioFactory;
        _catalogService = catalogService;
        _collectionManager = collectionManager;
        _libraryManager = libraryManager;
    }

    public async Task ImportCatalogAsync(string catalogId, string type, Guid userId, CancellationToken ct, IProgress<double>? progress = null) {
        {
            var catalogCfg = _catalogService.GetCatalogConfig(catalogId, type);
            if (catalogCfg == null) {
                _log.LogWarning("Catalog config not found for {Id} {Type}", catalogId, type);
                return;
            }

            if (!catalogCfg.Enabled) {
                _log.LogInformation("Catalog {Id} {Type} is disabled, skipping.", catalogId, type);
                return;
            }
            var cfg = GelatoPlugin.Instance!.GetConfig(Guid.Empty);
            var stremio = cfg.stremio;
            var seriesFolder = cfg.SeriesFolder;
            var movieFolder = cfg.MovieFolder;
            var createCollections = cfg.CreateCollections;
            var collectionMaxItems = cfg.MaxCollectionItems;

            if (seriesFolder is null) {
                _log.LogWarning("No series root folder found");
            }

            if (movieFolder is null) {
                _log.LogWarning("No movie root folder found");
            }


            var maxItems = catalogCfg.MaxItems > 0 ? catalogCfg.MaxItems : cfg.CatalogMaxItems;
            long done = 0;

            var opts = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct };

            var stopwatch = Stopwatch.StartNew();
            _log.LogInformation("Starting import for catalog {Name} ({Id}) - Limit: {Limit}", catalogCfg.Name, catalogId, maxItems);

            try {
                var skip = 0;
                var processed = 0;
                var importedIds = new ConcurrentBag<Guid>();

                while (processed < maxItems) {
                    ct.ThrowIfCancellationRequested();

                    var page = await stremio
                        .GetCatalogMetasAsync(catalogId, type, search: null, skip: skip)
                        .ConfigureAwait(false);

                    if (page is null || page.Count == 0) {
                        break;
                    }

                    await Parallel.ForEachAsync(
                        page,
                        opts,
                        async (meta, ct) => {
                            var p = Interlocked.Increment(ref processed);
                            ct.ThrowIfCancellationRequested();
                            if (p > maxItems) {
                                return;
                            }

                            //meta_ids.TryAdd(meta.Id, 0);
                            var mediaType = meta.Type;
                            var baseItemKind = mediaType.ToBaseItem();

                            // catalog can contain multiple types.

                            var root =
                                baseItemKind == BaseItemKind.Series ? seriesFolder
                                : baseItemKind == BaseItemKind.Movie ? movieFolder
                                : null;

                            if (root is not null) {
                                try {
                                    var (item, _) = await _manager
                                        .InsertMeta(
                                            root,
                                            meta,
                                            null,
                                            true,
                                            true,
                                            baseItemKind == BaseItemKind.Series,
                                            ct
                                        )
                                        .ConfigureAwait(false);

                                    if (item != null) importedIds.Add(item.Id);
                                }
                                catch (Exception ex) {
                                    _log.LogError(
                                        "{CatId}: insert meta failed for {Id}. Exception: {Message}\n{StackTrace}",
                                        catalogId,
                                        meta?.Id,
                                        ex.Message,
                                        ex.StackTrace
                                    );
                                }
                            }

                            var current = Interlocked.Increment(ref done);
                            progress?.Report(Math.Min(100, current / (double)maxItems * 100.0));
                        }
                    );

                    skip += page.Count;
                }

                if (catalogCfg.CreateCollection) {
                    await UpdateCollectionAsync(catalogCfg, importedIds.ToList())
                        .ConfigureAwait(false);
                    importedIds.Clear();
                }

                _log.LogInformation("{Id}: processed ({Count} items)", catalogCfg.Id, processed);
            }
            catch (OperationCanceledException ex) {
                _log.LogWarning(
                    ex,
                    "Catalog {Id} aborted due to non-user cancellation, continuing with next catalog",
                        catalogId
                );
            }
            catch (Exception ex) {
                _log.LogError(
                    ex,
                    "Catalog sync failed for {Id}: {Message}",
                    catalogCfg.Id,
                    ex.Message
                );
            }

            stopwatch.Stop();
            progress?.Report(100);
            _log.LogInformation(
                "Catalog {catalog} sync completed in {Minutes}m {Seconds}s ({TotalSeconds:F2}s total)",
                catalogCfg.Name,
                (int)stopwatch.Elapsed.TotalMinutes,
                stopwatch.Elapsed.Seconds,
                stopwatch.Elapsed.TotalSeconds
            );
        }
    }

    private async Task<BoxSet?> GetOrCreateBoxSetAsync(CatalogConfig config) {
        var id = $"{config.Type}.{config.Id}";
        var collection = _libraryManager.GetItemList(new InternalItemsQuery {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet },
            CollapseBoxSetItems = false,
            Recursive = true,
            HasAnyProviderId = new Dictionary<string, string> { { "Stremio", id } }
        }).OfType<BoxSet>().FirstOrDefault();

        if (collection is null) {
            collection = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions {
                Name = config.Name,
                IsLocked = true,
                ProviderIds = new Dictionary<string, string> { { "Stremio", id } }
            }).ConfigureAwait(false);

            collection.DisplayOrder = "Default";
            await collection.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
        }
        return collection;
    }

    private async Task UpdateCollectionAsync(CatalogConfig config, List<Guid> ids) {
        try {
            var collection = await GetOrCreateBoxSetAsync(config).ConfigureAwait(false);
            if (collection != null) {

                var currentChildren = _libraryManager.GetItemList(new InternalItemsQuery {
                    Parent = collection,
                    Recursive = false
                }).Select(i => i.Id).ToList();

                if (currentChildren.Any()) {
                    await _collectionManager.RemoveFromCollectionAsync(collection.Id, currentChildren).ConfigureAwait(false);
                }

                var itemsToAdd = ids.ToList();

                await _collectionManager.AddToCollectionAsync(collection.Id, itemsToAdd).ConfigureAwait(false);
                _log.LogInformation("Updated collection {Name} with {Count} items", config.Name, itemsToAdd.Count);
            }
        }
        catch (Exception ex) {
            _log.LogError(ex, "Error updating collection for {Name}", config.Name);
        }
    }

    public async Task SyncAllEnabledAsync(CancellationToken ct, IProgress<double>? progress = null) {
        // Force refresh of catalog list from manifest first
        var catalogs = await _catalogService.GetCatalogsAsync(Guid.Empty);
        var enabled = catalogs.Where(c => c.Enabled).ToList();

        int current = 0;
        int total = enabled.Count;

        foreach (var cat in enabled) {
            ct.ThrowIfCancellationRequested();
            _log.LogInformation("Processing enabled catalog: {Name}", cat.Name);

            // Create specific progress reporter for this catalog if needed, or just log
            await ImportCatalogAsync(cat.Id, cat.Type, Guid.Empty, ct).ConfigureAwait(false);

            current++;
            progress?.Report((double)current / total * 100);
        }
                    progress?.Report(100);
        await _libraryManager.ValidateMediaLibrary(new Progress<double>(), CancellationToken.None).ConfigureAwait(false);
    }
}
